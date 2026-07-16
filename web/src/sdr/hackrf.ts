/**
 * HackRF One WebUSB protocol layer — RECEIVE ONLY.
 * No transmit request code, no TX mode, no TX gain (Signal Atlas invariant #1).
 */

export interface HackRfDeviceInfo {
  boardId: number;
  firmwareVersion: string;
  serialNumber: string;
}

const HACKRF_VENDOR_ID = 0x1d50;
const HACKRF_PRODUCT_IDS = [0x6089, 0x604b];

export const HACKRF_FILTERS = HACKRF_PRODUCT_IDS.map((productId) => ({
  vendorId: HACKRF_VENDOR_ID,
  productId,
}));

const VALID_BASEBAND_BW = [
  1750000, 2500000, 3500000, 5000000, 5500000, 6000000, 7000000, 8000000,
  9000000, 10000000, 12000000, 14000000, 15000000, 20000000, 24000000, 28000000,
];

export function computeBasebandFilterBw(requestedHz: number): number {
  let best = VALID_BASEBAND_BW[0];
  for (const bw of VALID_BASEBAND_BW) {
    if (bw <= requestedHz) best = bw;
    else break;
  }
  return best;
}

// Receive-only subset of the HackRF vendor request codes (21 = SET_TXVGA_GAIN is deliberately absent).
enum HackRfRequest {
  SET_TRANSCEIVER_MODE = 1,
  SAMPLE_RATE_SET = 6,
  BASEBAND_FILTER_BANDWIDTH_SET = 7,
  BOARD_ID_READ = 14,
  VERSION_STRING_READ = 15,
  SET_FREQ = 16,
  AMP_ENABLE = 17,
  BOARD_PARTID_SERIALNO_READ = 18,
  SET_LNA_GAIN = 19,
  SET_VGA_GAIN = 20,
}

// TransceiverMode has no TX member: only OFF and RX exist in this build.
enum TransceiverMode {
  OFF = 0,
  RX = 1,
}

// eslint-disable-next-line @typescript-eslint/no-explicit-any
type AnyUSBDevice = any;

export class HackRfDevice {
  private device: AnyUSBDevice = null;
  private bulkInEndpoint = 0;
  private streaming = false;
  private onData: ((samples: Int8Array) => void) | null = null;
  private onEnd: ((error?: Error) => void) | null = null;
  private interfaceNumber = 0;

  get isConnected(): boolean {
    return this.device !== null && this.device.opened;
  }

  get usbDevice(): AnyUSBDevice {
    return this.device;
  }

  /** Prompt the browser device chooser, then open the selected HackRF. */
  async connect(): Promise<HackRfDeviceInfo> {
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    const nav = navigator as Navigator & { usb?: any };
    if (!nav.usb) {
      throw new Error(
        "WebUSB is not supported. Use Chrome or Edge. If the HackRF shows as a COM port, " +
          "install the WinUSB driver with Zadig (https://zadig.akeo.ie/).",
      );
    }
    try {
      this.device = await nav.usb.requestDevice({ filters: HACKRF_FILTERS });
    } catch (error) {
      if ((error as Error).name === "NotFoundError") {
        throw new Error(
          "No HackRF selected. Ensure it is plugged in, in HackRF mode, the WinUSB driver is " +
            "installed (Zadig), and no other SDR software is using it.",
        );
      }
      throw error;
    }
    return this.open();
  }

  /** Open a device already granted via navigator.usb.getDevices() (silent reconnect). */
  async adopt(device: AnyUSBDevice): Promise<HackRfDeviceInfo> {
    this.device = device;
    return this.open();
  }

  private async open(): Promise<HackRfDeviceInfo> {
    if (!this.device) throw new Error("No device selected.");
    await this.device.open();
    if (this.device.configuration === null) await this.device.selectConfiguration(1);

    const iface = this.device.configuration.interfaces[0];
    this.interfaceNumber = iface.interfaceNumber;
    await this.device.claimInterface(this.interfaceNumber);

    this.bulkInEndpoint = 0;
    for (const ep of iface.alternates[0].endpoints) {
      if (ep.type === "bulk" && ep.direction === "in") {
        this.bulkInEndpoint = ep.endpointNumber;
        break;
      }
    }
    if (!this.bulkInEndpoint) this.bulkInEndpoint = 1;

    return this.readDeviceInfo();
  }

  async disconnect(): Promise<void> {
    await this.stop();
    if (this.device) {
      try { await this.device.releaseInterface(this.interfaceNumber); } catch { /* ignore */ }
      try { await this.device.close(); } catch { /* ignore */ }
      this.device = null;
    }
  }

  private async controlIn(request: number, value: number, length: number, index = 0): Promise<DataView> {
    if (!this.device) throw new Error("Not connected");
    const result = await this.device.controlTransferIn(
      { requestType: "vendor", recipient: "device", request, value, index },
      length,
    );
    if (result.status !== "ok" || !result.data)
      throw new Error(`Control IN failed: request=${request} status=${result.status}`);
    return result.data;
  }

  private async controlOut(request: number, value: number, data?: BufferSource): Promise<void> {
    if (!this.device) throw new Error("Not connected");
    const result = await this.device.controlTransferOut(
      { requestType: "vendor", recipient: "device", request, value, index: 0 },
      data,
    );
    if (result.status !== "ok")
      throw new Error(`Control OUT failed: request=${request} status=${result.status}`);
  }

  private async readDeviceInfo(): Promise<HackRfDeviceInfo> {
    let boardId = 0;
    try { boardId = (await this.controlIn(HackRfRequest.BOARD_ID_READ, 0, 1)).getUint8(0); } catch { /* ignore */ }

    let firmwareVersion = "Unknown";
    try {
      const d = await this.controlIn(HackRfRequest.VERSION_STRING_READ, 0, 255);
      firmwareVersion = new TextDecoder().decode(d.buffer).replace(/\0+$/, "");
    } catch { /* ignore */ }

    let serialNumber = "Unknown";
    try {
      const d = await this.controlIn(HackRfRequest.BOARD_PARTID_SERIALNO_READ, 0, 24);
      const parts: string[] = [];
      for (let i = 8; i < 24; i += 4) parts.push(d.getUint32(i, true).toString(16).padStart(8, "0"));
      serialNumber = parts.join("").toUpperCase();
    } catch { /* ignore */ }

    return { boardId, firmwareVersion, serialNumber };
  }

  async setFrequency(freqHz: number): Promise<void> {
    const data = new ArrayBuffer(8);
    const view = new DataView(data);
    view.setUint32(0, Math.floor(freqHz / 1e6), true);
    view.setUint32(4, Math.floor(freqHz % 1e6), true);
    await this.controlOut(HackRfRequest.SET_FREQ, 0, data);
  }

  async setSampleRate(rateHz: number): Promise<void> {
    const data = new ArrayBuffer(8);
    const view = new DataView(data);
    view.setUint32(0, Math.floor(rateHz), true);
    view.setUint32(4, 1, true);
    await this.controlOut(HackRfRequest.SAMPLE_RATE_SET, 0, data);
  }

  async setBasebandFilter(bwHz: number): Promise<void> {
    const bw = computeBasebandFilterBw(Math.floor(bwHz));
    if (!this.device) throw new Error("Not connected");
    const result = await this.device.controlTransferOut({
      requestType: "vendor",
      recipient: "device",
      request: HackRfRequest.BASEBAND_FILTER_BANDWIDTH_SET,
      value: bw & 0xffff,
      index: (bw >> 16) & 0xffff,
    });
    if (result.status !== "ok") throw new Error(`setBasebandFilter failed: ${result.status}`);
  }

  async setLnaGain(gain: number): Promise<void> {
    const rounded = Math.min(40, Math.max(0, gain)) & ~0x07;
    await this.controlIn(HackRfRequest.SET_LNA_GAIN, 0, 1, rounded);
  }

  async setVgaGain(gain: number): Promise<void> {
    const rounded = Math.min(62, Math.max(0, gain)) & ~0x01;
    await this.controlIn(HackRfRequest.SET_VGA_GAIN, 0, 1, rounded);
  }

  async setAmpEnable(enabled: boolean): Promise<void> {
    await this.controlOut(HackRfRequest.AMP_ENABLE, enabled ? 1 : 0);
  }

  private async setTransceiverMode(mode: TransceiverMode): Promise<void> {
    await this.controlOut(HackRfRequest.SET_TRANSCEIVER_MODE, mode);
  }

  async startRx(onData: (samples: Int8Array) => void, onEnd?: (error?: Error) => void): Promise<void> {
    if (this.streaming) return;
    this.onData = onData;
    this.onEnd = onEnd ?? null;
    this.streaming = true;
    await this.setTransceiverMode(TransceiverMode.RX);
    void this.readLoop();
  }

  async stop(): Promise<void> {
    this.streaming = false;
    this.onEnd = null;
    try { await this.setTransceiverMode(TransceiverMode.OFF); } catch { /* ignore */ }
  }

  private async readLoop(): Promise<void> {
    while (this.streaming && this.device?.opened) {
      try {
        const result = await this.device.transferIn(this.bulkInEndpoint, 16384);
        if (result.status === "ok") {
          if (result.data && result.data.byteLength > 0) {
            const dv = result.data;
            this.onData?.(new Int8Array(dv.buffer, dv.byteOffset, dv.byteLength));
          }
        } else if (result.status === "stall" || result.status === "babble") {
          try { await this.device.clearHalt("in", this.bulkInEndpoint); } catch { /* ignore */ }
          await new Promise((resolve) => setTimeout(resolve, 2));
        }
      } catch (error) {
        if (this.streaming) {
          this.streaming = false;
          const notify = this.onEnd;
          this.onEnd = null;
          notify?.(error as Error);
        }
        break;
      }
    }
  }
}
