// Static band/channel preset catalog for the "frequency changer". Single source of
// truth for both the navbar BandSelector and the clickable coverage rows. Pure data +
// one pure function — no React, no backend. Band keys mirror the /spectrum/coverage
// band keys (src/SignalAtlas.Api/SpectrumSupport.cs Bands) so a coverage row joins to
// its preset by key; a mismatch simply renders no tune action (never crashes).

export interface ChannelPreset {
  key: string;
  label: string;
  centerFreqHz: number;
  sampleRateHz: number;
}

export interface BandPreset {
  key: string;
  label: string;
  lowHz: number;
  highHz: number;
  centerFreqHz: number;
  sampleRateHz: number;
  channels: ChannelPreset[];
}

const MS = 1_000_000;

export const bandPresets: BandPreset[] = [
  {
    key: "fm-broadcast",
    label: "FM broadcast 88-108 MHz",
    lowHz: 88 * MS,
    highHz: 108 * MS,
    centerFreqHz: 98 * MS,
    sampleRateHz: 10 * MS,
    channels: [
      { key: "fm-901", label: "90.1 MHz", centerFreqHz: 90_100_000, sampleRateHz: 2 * MS },
      { key: "fm-980", label: "98.0 MHz", centerFreqHz: 98_000_000, sampleRateHz: 2 * MS },
      { key: "fm-1045", label: "104.5 MHz", centerFreqHz: 104_500_000, sampleRateHz: 2 * MS },
    ],
  },
  {
    key: "adsb-1090",
    label: "ADS-B 1090 MHz",
    lowHz: 1_087_000_000,
    highHz: 1_093_000_000,
    centerFreqHz: 1_090_000_000,
    sampleRateHz: 2 * MS,
    channels: [
      { key: "adsb-1090", label: "1090 MHz", centerFreqHz: 1_090_000_000, sampleRateHz: 2 * MS },
    ],
  },
  {
    key: "ism-2400",
    label: "Wi-Fi / BLE / Zigbee 2.4 GHz",
    lowHz: 2_400_000_000,
    highHz: 2_485_000_000,
    centerFreqHz: 2_442_000_000,
    sampleRateHz: 20 * MS,
    channels: [
      { key: "wifi-ch1", label: "Wi-Fi ch 1 - 2412 MHz", centerFreqHz: 2_412_000_000, sampleRateHz: 20 * MS },
      { key: "wifi-ch6", label: "Wi-Fi ch 6 - 2437 MHz", centerFreqHz: 2_437_000_000, sampleRateHz: 20 * MS },
      { key: "wifi-ch11", label: "Wi-Fi ch 11 - 2462 MHz", centerFreqHz: 2_462_000_000, sampleRateHz: 20 * MS },
    ],
  },
  {
    key: "ism-sub-ghz",
    label: "ISM 902-928 MHz",
    lowHz: 902_000_000,
    highHz: 928_000_000,
    centerFreqHz: 915_000_000,
    sampleRateHz: 2 * MS,
    channels: [
      { key: "ism-915", label: "915 MHz", centerFreqHz: 915_000_000, sampleRateHz: 2 * MS },
    ],
  },
];

/** The band whose inclusive [lowHz, highHz] window contains centerHz, else null (=> "Custom"). */
export function activeBand(centerHz: number): BandPreset | null {
  return bandPresets.find((b) => centerHz >= b.lowHz && centerHz <= b.highHz) ?? null;
}
