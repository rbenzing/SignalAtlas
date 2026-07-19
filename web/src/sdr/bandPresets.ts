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
    label: "ISM 902-928 & 433 MHz",
    lowHz: 433_050_000,
    highHz: 928_000_000,
    centerFreqHz: 915_000_000,
    sampleRateHz: 2 * MS,
    channels: [
      { key: "ism-433", label: "433.92 MHz", centerFreqHz: 433_920_000, sampleRateHz: 2 * MS },
      { key: "ism-915", label: "915 MHz", centerFreqHz: 915_000_000, sampleRateHz: 2 * MS },
    ],
  },
  {
    key: "airband",
    label: "Airband 118-137 MHz (AM)",
    lowHz: 118 * MS,
    highHz: 137 * MS,
    centerFreqHz: 127_500_000,
    sampleRateHz: 19 * MS,
    channels: [
      { key: "airband-1215", label: "121.5 MHz - Emergency", centerFreqHz: 121_500_000, sampleRateHz: 2 * MS },
      { key: "airband-1180", label: "118.0 MHz", centerFreqHz: 118_000_000, sampleRateHz: 2 * MS },
    ],
  },
  {
    key: "marine-vhf",
    label: "Marine VHF 156-162 MHz",
    lowHz: 156 * MS,
    highHz: 162 * MS,
    centerFreqHz: 159_000_000,
    sampleRateHz: 6 * MS,
    channels: [
      { key: "marine-ch16", label: "Ch 16 - 156.8 MHz (Distress)", centerFreqHz: 156_800_000, sampleRateHz: 2 * MS },
    ],
  },
  {
    key: "noaa-weather",
    label: "NOAA Weather Radio 162.4-162.55 MHz",
    lowHz: 162_400_000,
    highHz: 162_550_000,
    centerFreqHz: 162_475_000,
    sampleRateHz: 2 * MS,
    channels: [
      { key: "noaa-wx1", label: "162.400 MHz", centerFreqHz: 162_400_000, sampleRateHz: 2 * MS },
      { key: "noaa-wx3", label: "162.475 MHz", centerFreqHz: 162_475_000, sampleRateHz: 2 * MS },
      { key: "noaa-wx7", label: "162.550 MHz", centerFreqHz: 162_550_000, sampleRateHz: 2 * MS },
    ],
  },
  {
    key: "amateur-2m",
    label: "2 m Amateur 144-148 MHz",
    lowHz: 144 * MS,
    highHz: 148 * MS,
    centerFreqHz: 146 * MS,
    sampleRateHz: 4 * MS,
    channels: [
      { key: "2m-ssb", label: "144.200 MHz - SSB calling", centerFreqHz: 144_200_000, sampleRateHz: 2 * MS },
      { key: "2m-fm", label: "146.520 MHz - FM simplex", centerFreqHz: 146_520_000, sampleRateHz: 2 * MS },
    ],
  },
  {
    key: "amateur-70cm",
    label: "70 cm Amateur 430-440 MHz",
    lowHz: 430 * MS,
    highHz: 440 * MS,
    centerFreqHz: 435 * MS,
    sampleRateHz: 10 * MS,
    channels: [
      { key: "70cm-ssb", label: "433.500 MHz - SSB calling", centerFreqHz: 433_500_000, sampleRateHz: 2 * MS },
      { key: "70cm-fm", label: "435.000 MHz", centerFreqHz: 435_000_000, sampleRateHz: 2 * MS },
    ],
  },
  {
    key: "gps-l1",
    label: "GPS L1 1575.42 MHz",
    lowHz: 1_574_420_000,
    highHz: 1_576_420_000,
    centerFreqHz: 1_575_420_000,
    sampleRateHz: 2 * MS,
    channels: [
      { key: "gps-l1-ca", label: "1575.42 MHz - L1 C/A", centerFreqHz: 1_575_420_000, sampleRateHz: 2 * MS },
    ],
  },
  {
    key: "dab-band3",
    label: "DAB / Band III 174-240 MHz",
    lowHz: 174 * MS,
    highHz: 240 * MS,
    centerFreqHz: 207 * MS,
    sampleRateHz: 8 * MS,
    channels: [
      { key: "dab-11a", label: "Block 11A - 215.072 MHz", centerFreqHz: 215_072_000, sampleRateHz: 2 * MS },
      { key: "dab-12a", label: "Block 12A - 223.936 MHz", centerFreqHz: 223_936_000, sampleRateHz: 2 * MS },
    ],
  },
  {
    key: "wifi-5ghz",
    label: "Wi-Fi 5 GHz 5150-5850 MHz",
    lowHz: 5_150 * MS,
    highHz: 5_850 * MS,
    centerFreqHz: 5_500 * MS,
    sampleRateHz: 20 * MS,
    channels: [
      { key: "wifi5-ch36", label: "Ch 36 - 5180 MHz", centerFreqHz: 5_180 * MS, sampleRateHz: 20 * MS },
      { key: "wifi5-ch100", label: "Ch 100 - 5500 MHz", centerFreqHz: 5_500 * MS, sampleRateHz: 20 * MS },
      { key: "wifi5-ch165", label: "Ch 165 - 5825 MHz", centerFreqHz: 5_825 * MS, sampleRateHz: 20 * MS },
    ],
  },
];

/**
 * The band whose inclusive [lowHz, highHz] window contains centerHz, else null (=> "Custom").
 * When multiple bands' windows contain centerHz (e.g. the 70 cm amateur band sits inside the
 * wider sub-GHz ISM window), the NARROWEST (most specific) matching window wins, not the first
 * one in catalog order — otherwise a broad band can silently shadow a narrower one added later.
 */
export function activeBand(centerHz: number): BandPreset | null {
  let best: BandPreset | null = null;
  for (const b of bandPresets) {
    if (centerHz < b.lowHz || centerHz > b.highHz) continue;
    if (best === null || b.highHz - b.lowHz < best.highHz - best.lowHz) best = b;
  }
  return best;
}
