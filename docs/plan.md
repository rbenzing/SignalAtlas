# Signal Atlas

## Living Map of the Radio Spectrum

Version: 0.1
Status: Founder Vision + AI Agent Implementation Plan

---

# Mission

Signal Atlas answers a simple question:

> What is happening in the radio spectrum around me right now?

The system continuously observes RF activity using software-defined radios (SDRs), classifies signals, identifies emitters, tracks behavior over time, and presents the information as a human-readable geographic map.

Signal Atlas is not an SDR tool.

Signal Atlas is an RF Intelligence Platform.

---

# Core Product Principles

## Principle 1

Humans do not think in frequencies.

Humans think in:

* Devices
* Locations
* Activity
* Changes

Bad:

915.125 MHz @ -62 dBm

Good:

Weather Station detected near Oak Street.

---

## Principle 2

Every signal becomes an object.

Instead of:

Spectrum Energy

We model:

Emitter
Device
Protocol
Behavior

---

## Principle 3

Every observation is historical.

The platform must support:

* Replay
* Time travel
* Trend analysis
* Change detection

Historical context is a first-class feature.

---

## Principle 4

Explainable AI only.

Every classification must include evidence.

Example:

Classification:
LoRa

Confidence:
92%

Evidence:

* Chirp modulation
* 125 kHz bandwidth
* Repeating burst pattern

---

# High-Level Architecture

```text
HackRF / SDR
      |
      V
Spectrum Collector
      |
      V
Signal Processing Engine
      |
      +-------------------+
      |                   |
      V                   V
Classification      Anomaly Detection
      |                   |
      +-------------------+
              |
              V
Emitter Correlation Engine
              |
              V
Geospatial Engine
              |
              V
Signal Atlas API
              |
              V
React Frontend
```

---

# System Components

## 1. SDR Collector

Responsibilities:

* SDR management
* Frequency scanning
* IQ sample capture
* GPS integration
* Sample storage

Inputs:

* HackRF
* GPS receiver

Outputs:

Observation records

```json
{
  "timestamp": "",
  "latitude": 0,
  "longitude": 0,
  "frequency": 915000000,
  "power": -65
}
```

---

## 2. Signal Processing Engine

Responsibilities:

* FFT generation
* Spectrogram generation
* Occupancy calculation
* Noise floor estimation
* Feature extraction

Outputs:

```json
{
  "bandwidth": 125000,
  "peakPower": -58,
  "snr": 12.5,
  "duration": 350
}
```

---

## 3. Classification Engine

Purpose:

Convert RF energy into protocol candidates.

Examples:

* Wi-Fi
* Bluetooth
* LoRa
* Zigbee
* ADS-B
* FM Radio
* Unknown

Outputs:

```json
{
  "classification": "LoRa",
  "confidence": 0.92
}
```

---

## 4. Emitter Correlation Engine

Purpose:

Determine whether multiple observations belong to the same emitter.

Factors:

* Frequency stability
* Bandwidth
* Modulation
* Location
* Time patterns

Output:

Emitter ID

```json
{
  "emitterId": "EMT-001024"
}
```

---

## 5. Geospatial Engine

Purpose:

Convert observations into geographic intelligence.

Capabilities:

* Heat maps
* Coverage maps
* Density maps
* Historical maps
* Source estimation

Outputs:

Probable emitter locations.

---

## 6. Behavior Engine

Purpose:

Learn emitter behavior.

Examples:

Periodic transmitter

Every 5 minutes

Always-on infrastructure

24/7 activity

Mobile emitter

Moves through city

Outputs:

Behavior profiles

---

## 7. Anomaly Engine

Purpose:

Detect unusual activity.

Examples:

* New emitter
* Protocol change
* Location change
* Power change
* Occupancy spike

Outputs:

Alerts

---

# Signal Layers

Signal Layers are the primary user abstraction.

---

## Layer 1: Spectrum Layer

Raw RF activity.

Displays:

* Waterfall
* Spectrogram
* Occupancy

---

## Layer 2: Protocol Layer

Displays:

* Wi-Fi
* Bluetooth
* LoRa
* Zigbee
* Unknown

---

## Layer 3: Device Layer

Displays likely devices.

Examples:

* Router
* Weather station
* Garage opener
* Smart sensor

---

## Layer 4: Emitter Layer

Displays unique emitters.

Each emitter receives:

* Unique ID
* History
* Location
* Confidence

---

## Layer 5: Activity Layer

Answers:

What changed?

Examples:

* New emitter detected
* Emitter disappeared
* Traffic increase

---

## Layer 6: Temporal Layer

Answers:

What happened yesterday?

Supports replay and comparison.

---

## Layer 7: Behavior Layer

Answers:

What patterns exist?

Examples:

* Periodic
* Mobile
* Stationary
* Burst activity

---

## Layer 8: Geographic Layer

Map visualization.

Displays:

* Emitters
* Heatmaps
* Density clusters
* Movement

---

## Layer 9: Semantic Layer

Natural language summaries.

Example:

"There are 31 active Wi-Fi access points, 12 Bluetooth devices, and 4 unidentified emitters within the current area."

---

# Frontend

Technology:

* React
* TypeScript
* MUI
* MapLibre GL
* Recharts
* SignalR

Views:

1. Live Spectrum
2. RF Map
3. Emitters
4. Timeline
5. Alerts
6. AI Investigator

---

# Backend

Technology:

* ASP.NET Core
* PostgreSQL
* TimescaleDB
* Redis
* gRPC

Services:

* Ingestion Service
* Classification Service
* Correlation Service
* Geospatial Service
* Alert Service

---

# Data Model

Observation

Raw RF measurement

Signal

Classified observation

Emitter

Persistent object representing a transmitter

Behavior Profile

Learned characteristics

Alert

Anomaly event

---

# AI Roadmap

Phase 1

Rule-based classification

Phase 2

ML-assisted protocol classification

Phase 3

Emitter fingerprinting

Phase 4

Behavior prediction

Phase 5

Natural language spectrum analyst

---

# MVP Success Criteria

The MVP succeeds when a user can:

1. Walk or drive with a HackRF.
2. Collect RF observations.
3. Generate a map.
4. See detected protocols.
5. View emitter history.
6. Identify new activity.
7. Ask:

"What changed in the spectrum today?"

And receive a meaningful answer.

---

# Long-Term Vision

Signal Atlas becomes a living digital twin of the RF environment.

The platform continuously answers:

* What exists?
* Where is it?
* What is it doing?
* What changed?
* What should I investigate?

The end state is a real-time, explainable, searchable map of the radio spectrum.
