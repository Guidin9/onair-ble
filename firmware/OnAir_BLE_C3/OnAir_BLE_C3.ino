/*
 * ============================================================================
 *  On Air - BLE Status Indicator Firmware  (power-optimised revision)
 *  Target : ESP32-C3 SuperMini (RISC-V, BLE 5.0 only, no Classic BT)
 *  IDE    : Arduino IDE / arduino-cli
 * ============================================================================
 *
 *  BEHAVIOUR
 *    boot / disconnect : two short dim pulses every 3 s for SEARCH_WINDOW_MS,
 *                        then dark   ("awake, looking for the PC")
 *    connected + idle  : everything dark, CPU in automatic light sleep
 *    camera or mic on  : all 8 red LEDs breathing (smooth fade in/out)
 *
 *    This board carries red LEDs only -- no white ones -- so both indications
 *    share the same eight LEDs. That makes the shape of each one load-bearing:
 *    a steady or breathing red announces "camera live" to everyone in the
 *    room, so "not connected yet" has to be brief and dim instead. Two short
 *    pulses read as a status blink; nothing else may.
 *
 *  ---------------------------------------------------------------------------
 *  UPGRADING FROM THE PREVIOUS REVISION WITHOUT LOSING THE PAIRING
 *  ---------------------------------------------------------------------------
 *    Bonds live in the NVS partition. A normal Arduino upload rewrites only
 *    the app partition and leaves NVS alone, so the existing pairing with the
 *    PC survives -- PROVIDED that:
 *
 *      Tools -> "Erase All Flash Before Sketch Upload"  ==  Disabled
 *
 *    If that is set to Enabled, NVS is wiped and you have to re-pair.
 *    The service/characteristic UUIDs, the GATT layout, the device name and
 *    the BLE MAC are all unchanged in this revision, so Windows' cached GATT
 *    table stays valid and the PC-side app needs no changes.
 *
 *  ---------------------------------------------------------------------------
 *  WIRING  (8 red LEDs, 2 per pin -- verified with LED_Breathing_Test.ino)
 *  ---------------------------------------------------------------------------
 *    GPIO5  -> 330R -> RED LED x2 (one resistor EACH) -> GND
 *    GPIO6  -> 330R -> RED LED x2 (one resistor EACH) -> GND
 *    GPIO7  -> 330R -> RED LED x2 (one resistor EACH) -> GND
 *    GPIO10 -> 330R -> RED LED x2 (one resistor EACH) -> GND
 *    GPIO3  -> battery sense, 100K/100K divider from the switched battery rail,
 *              plus 100 nF from GPIO3 to GND -- a 50K source is too high for
 *              the ADC's sample-and-hold and reads low without it
 *    GPIO9  -> BOOT button, active LOW (hold 3 s to clear bonds)
 *
 *  ---------------------------------------------------------------------------
 *  POWER DESIGN
 *  ---------------------------------------------------------------------------
 *    The dominant consumer in the previous revision was not the LEDs, it was
 *    the CPU: light sleep had to stay off because an always-lit indication is
 *    driven by LEDC, which stops during light sleep. Now that every indication
 *    ends in darkness -- the search pulses expire, the breathing curve only
 *    runs while the camera is live -- the CPU can sleep for the vast majority
 *    of the device's life.
 *
 *    Light sleep is therefore switched dynamically:
 *      LED_STANDBY   -> light sleep ON   (no PWM running, nothing to flicker)
 *      LED_SEARCHING -> light sleep OFF  (pulse PWM active)
 *      LED_BREATHING -> light sleep OFF  (breathing PWM active)
 *
 *    Equally important: loop() now ends in delay(). On ESP32 Arduino, delay()
 *    maps to vTaskDelay, which lets FreeRTOS reach tickless idle and actually
 *    enter light sleep. A loop that only polls millis() and returns spins the
 *    CPU at 100% and defeats power management entirely -- that was the case
 *    before, and fixing it matters more than any LED change.
 *
 *    IMPORTANT -- automatic light sleep needs CONFIG_PM_ENABLE, and the stock
 *    Arduino-ESP32 core ships with it OFF ("# CONFIG_PM_ENABLE is not set" in
 *    tools/esp32c3-libs/<ver>/sdkconfig). On that core esp_pm_configure()
 *    returns ESP_ERR_NOT_SUPPORTED, the request is logged once, and the chip
 *    simply never sleeps. Nothing breaks -- only the budget changes:
 *
 *    8 red LEDs at 330R peak at roughly 34 mA together, and the gamma-corrected
 *    curve averages 3/8 of its peak (mean of sin^4 over a period), so the LEDs
 *    add ~13 mA while breathing -- less than the CPU does.
 *
 *      stock Arduino core (no PM), CPU pinned to 80 MHz:
 *        idle ~20 mA x 22 h + breathing ~33 mA x 2 h  =  ~505 mAh/day
 *        -> roughly 6 days on 3000 mAh.
 *      PM enabled (custom sdkconfig / PlatformIO / ESP-IDF):
 *        standby ~2 mA x 22 h + breathing ~33 mA x 2 h  =  ~110 mAh/day
 *        -> roughly 4 weeks on 3000 mAh.
 *
 *    The LED work below is worth it either way, but the four-week figure is
 *    only reachable on a build with power management compiled in.
 *    Leaving the board's power LED in place adds 1-3 mA around the clock on
 *    top of that. Desolder it.
 *
 *  ---------------------------------------------------------------------------
 *  ASSUMPTIONS MADE (explicitly flagged, per spec):
 *  ---------------------------------------------------------------------------
 *    [A1] All LEDs are active-HIGH (anode towards the GPIO, cathode to GND).
 *         Every LED has its OWN series resistor; sharing one between parallel
 *         LEDs lets the lowest-Vf part hog the current and fail early.
 *         All eight are red: there is no white channel on this board, which is
 *         why the not-connected indication is a pulse pattern rather than a
 *         second colour.
 *    [A2] No external 32.768 kHz crystal -> the RTC slow clock is the internal
 *         RC oscillator, calibrated by the IDF. This matters again now that
 *         light sleep is back on: BLE anchor timing during sleep depends on
 *         it. If the link becomes unstable, this is the first suspect --
 *         comment out ENABLE_LIGHT_SLEEP to confirm.
 *    [A3] The 30 min timeout is measured from the LAST WRITE to `state`,
 *         not from the last LED change. A host that keeps re-writing the same
 *         value keeps the active state alive.
 *    [A4] Battery percentage uses a linear 3.40 V - 4.20 V map.
 *    [A5] Light sleep is REQUESTED dynamically rather than disabled outright
 *         (supersedes the previous revision's [A9]). Whether the request is
 *         honoured depends on CONFIG_PM_ENABLE in the core build -- see the
 *         power section above. setCpuFrequencyMhz() in setup() is the part
 *         that works on every build.
 *    [A6] Non-bonded writes are rejected in two layers: WRITE_ENC on the
 *         characteristic, plus connInfo.isBonded() in the write callback.
 *    [A7] The device name lives in the SCAN RESPONSE packet, not the main
 *         advertisement. Flags (3 B) + a 128-bit service UUID (18 B) is 21 of
 *         the 31 bytes, so "On Air" (8 B) would in fact still fit -- but
 *         keeping the connectable advertisement down to just the UUID leaves
 *         room for a longer name later and keeps every advertising event
 *         short. The name has to be SOMEWHERE, though: omit it entirely and
 *         Windows shows "Unknown device".
 *    [A8] Battery sense is on GPIO3 (ADC1_CH3). ADC2 channels are unusable
 *         while the radio is active.
 *    [A9] BLE TX power is reduced from +9 dBm to +3 dBm. This is a desk device
 *         sitting a metre or two from the PC, so the extra range buys nothing
 *         and costs current on every connection event. If reconnection
 *         reliability gets worse after this revision, set BLE_TX_POWER back to
 *         9 -- that is the one line to try first.
 *         NOTE: NimBLE 2.x takes dBm here, NOT an esp_power_level_t. Passing
 *         ESP_PWR_LVL_P3 (numerically 9 on the C3) asks for +9 dBm, i.e. the
 *         exact opposite of what this assumption claims.
 *   [A10] Connection parameters are relaxed (60-120 ms interval, latency 4)
 *         so the radio wakes roughly every 600 ms instead of every 60 ms.
 *         Worst-case added delay before a state write is applied is therefore
 *         ~0.6 s, which is well inside the 1-3 s responsiveness already
 *         accepted for this product.
 *   [A11] The breathing curve is gamma-corrected (squared sine). A linear duty
 *         ramp looks wrong to the eye, which perceives brightness roughly
 *         logarithmically -- it appears to snap on and linger at full.
 * ============================================================================
 */

#include <NimBLEDevice.h>
#include "esp_pm.h"
#include <math.h>

/* ==========================================================================
 *  USER-TUNABLE CONFIGURATION  -- everything you may want to change is here
 * ========================================================================== */

/* ---- Debug ---- */
#define DEBUG_SERIAL                 // comment out this line for production
#define SERIAL_BAUD              115200

/* ---- Power management ---- */
#define ENABLE_LIGHT_SLEEP           // comment out if the BLE link misbehaves
#define CPU_MAX_FREQ_MHZ         80
#define CPU_MIN_FREQ_MHZ         10

/* ---- Pins ----
 * All four channels drive red LEDs, 2 per pin, 330R per LED. Same pins and
 * same order as LED_Breathing_Test.ino, so a channel proven with that sketch
 * is proven here. */
#define LED_PIN_1                5
#define LED_PIN_2                6
#define LED_PIN_3                7
#define LED_PIN_4                10
#define ADC_PIN                  3   // battery sense, ADC1_CH3, 100K/100K divider
#define BUTTON_PIN               9   // BOOT button, active LOW, internal pull-up
/* Do NOT use: GPIO2 / GPIO8 (strapping), GPIO20 / GPIO21 (UART0),
 *             GPIO18 / GPIO19 (native USB D-/D+). */

/* ---- "Awake, looking for the PC" indication ----
 * Two short pulses, then a long gap, repeated for SEARCH_WINDOW_MS. Keep it
 * dim and keep it brief: these are the same LEDs the on-air indication uses,
 * and the two must never be confusable across a room. */
#define SEARCH_WINDOW_MS          60000UL // how long to pulse after boot/disconnect
#define SEARCH_OFF_ON_CONNECT     1       // 1 = go dark as soon as the PC connects
#define SEARCH_BRIGHTNESS_PERCENT 15      // 1..100, keep well under the breathing peak
#define SEARCH_PULSE_MS           120UL   // length of a single pulse
#define SEARCH_GAP_MS             180UL   // dark time between the two pulses
#define SEARCH_PERIOD_MS          3000UL  // the pulse pair repeats this often

/* ---- Red breathing ---- */
#define BREATH_PERIOD_MS         3000UL  // one full fade in + fade out cycle
#define BREATH_UPDATE_MS         20UL    // 50 Hz, same as LED_Breathing_Test.ino
#define RED_MAX_BRIGHTNESS_PERCENT 100   // peak of the breathing curve

/* ---- LEDC (hardware PWM) ---- */
#define LEDC_FREQ_HZ             1000    // far above the flicker fusion threshold
#define LEDC_RESOLUTION_BITS     8       // 0..255 duty range

/* ---- Loop cadence (drives how deeply the CPU can sleep) ---- */
#define LOOP_DELAY_STANDBY_MS    250     // long sleeps when nothing is animating
#define LOOP_DELAY_ANIMATING_MS  5       // short while PWM curves are running.
                                         // Deliberately shorter than
                                         // BREATH_UPDATE_MS: polling at the
                                         // same rate as the update gate makes
                                         // frames land at 20 or 40 ms at
                                         // random, which is visible as jitter.

/* ---- BLE connection parameters ---- [A10]
 * Units: interval 1.25 ms, timeout 10 ms.
 * Effective radio wake period = max_interval x (1 + latency). */
#define CONN_MIN_INTERVAL        48      // 60 ms
#define CONN_MAX_INTERVAL        96      // 120 ms
#define CONN_LATENCY             4       // -> wakes roughly every 600 ms
#define CONN_TIMEOUT             500     // 5 s supervision timeout

/* ---- BLE TX power, in dBm ---- [A9]
 * NimBLE 2.x: setPower(int8_t dbm) -> esp_power_level_t(dbm / 3 + ESP_PWR_LVL_N0).
 * It wants dBm, not an enum. Multiples of 3 only; anything else is rounded up. */
#define BLE_TX_POWER             3       // dBm. Raise to 9 if range suffers.

/* ---- Timing ---- */
#define TIMEOUT_MS               (30UL * 60UL * 1000UL)  // 30 min failsafe
#define ADVERTISING_INTERVAL_MS  1000UL                  // idle advertising
#define BATTERY_READ_INTERVAL_MS 300000UL                // 5 min; battery is slow
#define BUTTON_HOLD_MS           3000UL                  // hold to clear bonds
#define BUTTON_DEBOUNCE_MS       50UL

/* ---- Battery ---- */
#define BATTERY_DIVIDER_RATIO    2.0f    // 100K + 100K -> reads half of Vbat
#define BATTERY_SAMPLES          12      // oversampling count
#define BATTERY_MIN_MV           3400    // 0%
#define BATTERY_MAX_MV           4200    // 100%

/* ---- BLE identity -- DO NOT CHANGE, the PC-side app depends on these ---- */
#define BLE_DEVICE_NAME          "On Air"
#define ONAIR_SERVICE_UUID       "6f2a1b40-9c3e-4d17-8a52-b1c7e0f43d91"
#define ONAIR_STATE_CHAR_UUID    "6f2a1b41-9c3e-4d17-8a52-b1c7e0f43d91"
#define BATTERY_SERVICE_UUID     (uint16_t)0x180F
#define BATTERY_LEVEL_CHAR_UUID  (uint16_t)0x2A19

/* ---- state bitfield ---- */
#define STATE_BIT_CAMERA         0x01   // bit0
#define STATE_BIT_MICROPHONE     0x02   // bit1
#define STATE_ACTIVE_MASK        (STATE_BIT_CAMERA | STATE_BIT_MICROPHONE)

/* ==========================================================================
 *  DEBUG MACROS
 * ========================================================================== */
#define STRINGIFY_(x) #x
#define STRINGIFY(x)  STRINGIFY_(x)

#ifdef DEBUG_SERIAL
  #define DBG_BEGIN()   Serial.begin(SERIAL_BAUD)
  #define DBG(x)        Serial.print(x)
  #define DBGLN(x)      Serial.println(x)
  #define DBGF(...)     Serial.printf(__VA_ARGS__)
#else
  #define DBG_BEGIN()
  #define DBG(x)
  #define DBGLN(x)
  #define DBGF(...)
#endif

/* ==========================================================================
 *  GLOBAL STATE
 * ========================================================================== */

static NimBLEServer*         g_server      = nullptr;
static NimBLECharacteristic* g_stateChar   = nullptr;
static NimBLECharacteristic* g_batteryChar = nullptr;

static volatile uint8_t  g_state            = 0x00;
static volatile uint32_t g_lastStateWriteMs = 0;
static volatile bool     g_connected        = false;

static uint32_t g_lastBatteryReadMs = 0;
static uint8_t  g_batteryPercent    = 0;

/* Button state machine */
static bool     g_btnStableLow   = false;
static bool     g_btnLastRaw     = false;
static uint32_t g_btnChangeMs    = 0;
static uint32_t g_btnPressStart  = 0;
static bool     g_btnActionFired = false;

/* LED mode machine */
enum LedMode : uint8_t {
  LED_STANDBY,        // everything dark, light sleep permitted
  LED_SEARCHING,      // dim pulse pairs, startup / disconnected indication
  LED_BREATHING       // full breathing curve, camera or mic active
};
static LedMode  g_ledMode      = LED_STANDBY;
static bool     g_lightSleepOn = false;

/* Search window timer (overflow-safe: start + elapsed, never an absolute end).
 * g_searchStartMs doubles as the phase anchor for the pulse pattern.
 * Written from the NimBLE host task (connect/disconnect) and read from loop(),
 * hence volatile. The C3 is single-core, so an aligned word and a bool need no
 * heavier synchronisation than that. */
static volatile uint32_t g_searchStartMs = 0;
static volatile bool     g_searchActive  = false;

/* Breathing engine */
static uint32_t g_breathStartMs = 0;
static uint32_t g_breathLastMs  = 0;

/* One-shot acknowledgement blink (bond erase). Overrides everything. */
static uint8_t  g_ackRemaining = 0;
static uint32_t g_ackLastMs    = 0;
static bool     g_ackOn        = false;
#define ACK_HALF_PERIOD_MS      150UL
#define ACK_BRIGHTNESS_PERCENT  50    // brighter than a search pulse, dimmer
                                      // than on-air: this is a reply to a
                                      // button press, not a status

/* ==========================================================================
 *  POWER MANAGEMENT
 * ========================================================================== */

/*
 * Enables or disables automatic light sleep. Called on every LED mode change:
 * PWM peripherals stop during light sleep, so any mode that animates an LED
 * has to keep the CPU awake.
 */
static void setLightSleep(bool enable) {
#ifdef ENABLE_LIGHT_SLEEP
  if (enable == g_lightSleepOn) return;

  esp_pm_config_t pm = {
    .max_freq_mhz       = CPU_MAX_FREQ_MHZ,
    .min_freq_mhz       = enable ? CPU_MIN_FREQ_MHZ : CPU_MAX_FREQ_MHZ,
    .light_sleep_enable = enable
  };
  esp_err_t err = esp_pm_configure(&pm);
  if (err == ESP_OK) {
    g_lightSleepOn = enable;
    DBGF("[PM] light sleep %s\n", enable ? "ON" : "OFF");
    return;
  }

  /* ESP_ERR_NOT_SUPPORTED means CONFIG_PM_ENABLE is off in the core build,
   * which is the case for the stock Arduino-ESP32 core. The firmware still
   * works, it just never sleeps -- so say that once, rather than on every LED
   * transition, where it would bury the rest of the log. */
  if (err == ESP_ERR_NOT_SUPPORTED) {
    static bool warned = false;
    if (!warned) {
      warned = true;
      DBGLN("[PM] CONFIG_PM_ENABLE is off in this core build: no light sleep. "
            "Expect ~20 mA idle instead of ~2 mA (CPU stays at "
            STRINGIFY(CPU_MAX_FREQ_MHZ) " MHz).");
    }
    return;
  }

  DBGF("[PM] esp_pm_configure failed: %d\n", (int)err);
#else
  (void)enable;
#endif
}

/* ==========================================================================
 *  LED ABSTRACTION
 *  All four channels drive red LEDs through LEDC, so every indication -- the
 *  breathing curve, the search pulses, the acknowledgement blink -- is one
 *  duty value written to all pins at once. This block is the only place to
 *  edit if the discrete LEDs are ever swapped for WS2812 strips.
 * ========================================================================== */

static const uint8_t LED_PINS[] = { LED_PIN_1, LED_PIN_2, LED_PIN_3, LED_PIN_4 };
static const uint8_t LED_COUNT  = sizeof(LED_PINS) / sizeof(LED_PINS[0]);

static uint16_t dutyFromPercent(uint32_t percent) {
  uint32_t maxDuty = (1UL << LEDC_RESOLUTION_BITS) - 1UL;
  if (percent > 100) percent = 100;
  return (uint16_t)((maxDuty * percent) / 100UL);
}

static void ledInit() {
  for (uint8_t i = 0; i < LED_COUNT; i++) {
    /* ledcAttach() is the ESP32 core 3.x API. On core 2.x this would be
     * ledcSetup() + ledcAttachPin(). */
    ledcAttach(LED_PINS[i], LEDC_FREQ_HZ, LEDC_RESOLUTION_BITS);
    ledcWrite(LED_PINS[i], 0);
  }
}

static void ledsSetDuty(uint16_t duty) {
  for (uint8_t i = 0; i < LED_COUNT; i++) {
    ledcWrite(LED_PINS[i], duty);
  }
}

static void allLedsOff() {
  ledsSetDuty(0);
}

static bool stateIsActive() {
  return (g_state & STATE_ACTIVE_MASK) != 0;
}

/* Starts (or restarts) the search indication window. */
static void startSearchWindow(const char* reason) {
  g_searchStartMs = millis();
  g_searchActive  = true;
  DBGF("[LED] search window started (%s)\n", reason);
}

static void cancelSearchWindow() {
  g_searchActive = false;
}

static bool searchWindowRunning() {
  if (!g_searchActive) return false;
  if ((uint32_t)(millis() - g_searchStartMs) >= SEARCH_WINDOW_MS) {
    g_searchActive = false;             // expired
    return false;
  }
  return true;
}

/*
 * Decides which LED mode should be active and performs the transition.
 * Called from loop() and whenever the state changes.
 */
static void ledUpdateMode() {
  if (g_ackRemaining > 0) return;       // acknowledgement blink owns the LEDs

  LedMode want;
  if (stateIsActive())            want = LED_BREATHING;
  else if (searchWindowRunning()) want = LED_SEARCHING;
  else                            want = LED_STANDBY;

  if (want == g_ledMode) return;        // nothing to do
  g_ledMode = want;

  switch (want) {
    case LED_BREATHING:
      setLightSleep(false);             // PWM must keep running
      g_breathStartMs = millis();
      g_breathLastMs  = 0;              // force an immediate first update
      DBGLN("[LED] mode: RED breathing");
      break;

    case LED_SEARCHING:
      setLightSleep(false);
      /* serviceSearching() owns the duty from here. Start dark so the first
       * pulse has a clean edge whatever the LEDs were doing before. */
      ledsSetDuty(0);
      DBGLN("[LED] mode: SEARCHING (dim pulse pairs)");
      break;

    case LED_STANDBY:
    default:
      allLedsOff();
      setLightSleep(true);              // nothing animating, let the CPU sleep
      DBGLN("[LED] mode: STANDBY (dark)");
      break;
  }
}

/* Drives the red breathing curve while LED_BREATHING is active. [A11] */
static void serviceBreathing() {
  if (g_ledMode != LED_BREATHING) return;
  if (g_ackRemaining > 0) return;

  uint32_t now = millis();
  if (g_breathLastMs != 0 && (now - g_breathLastMs) < BREATH_UPDATE_MS) return;
  g_breathLastMs = now;

  uint32_t elapsed = (now - g_breathStartMs) % BREATH_PERIOD_MS;
  float phase = (float)elapsed / (float)BREATH_PERIOD_MS;

  /* Raised cosine gives a smooth 0 -> 1 -> 0 sweep with no hard edges. */
  float linear = (1.0f - cosf(2.0f * (float)M_PI * phase)) * 0.5f;

  /* Gamma correction: the eye is roughly logarithmic, so a linear duty ramp
   * looks like it snaps on and then sits at full. Squaring restores an even
   * perceived fade. */
  float perceived = linear * linear;

  uint16_t peak = dutyFromPercent(RED_MAX_BRIGHTNESS_PERCENT);
  ledsSetDuty((uint16_t)(perceived * (float)peak));
}

/*
 * Drives the search pattern while LED_SEARCHING is active: two SEARCH_PULSE_MS
 * pulses separated by SEARCH_GAP_MS, at the top of every SEARCH_PERIOD_MS.
 *
 * The shape is the whole point. With no white channel on this board the search
 * indication borrows the on-air LEDs, so it has to be unmistakable at a
 * glance: short, dim, and clearly repeating. Anything steady or slow-fading
 * would tell the room the camera is live while the PC is not even connected.
 */
static void serviceSearching() {
  if (g_ledMode != LED_SEARCHING) return;
  if (g_ackRemaining > 0) return;

  uint32_t pos = (uint32_t)(millis() - g_searchStartMs) % SEARCH_PERIOD_MS;

  bool on = (pos < SEARCH_PULSE_MS) ||
            (pos >= SEARCH_PULSE_MS + SEARCH_GAP_MS &&
             pos <  SEARCH_PULSE_MS + SEARCH_GAP_MS + SEARCH_PULSE_MS);

  ledsSetDuty(on ? dutyFromPercent(SEARCH_BRIGHTNESS_PERCENT) : 0);
}

/* Acknowledgement blink (bond erase feedback). Fast and mid-brightness, so it
 * reads as a reply to the button rather than as either status indication. */
static void ledStartAck(uint8_t blinks) {
  g_ackRemaining = blinks * 2;          // count half-transitions
  g_ackLastMs    = millis();
  g_ackOn        = true;
  setLightSleep(false);                 // PWM has to run during the blink
  ledsSetDuty(dutyFromPercent(ACK_BRIGHTNESS_PERCENT));
}

static void serviceAck() {
  if (g_ackRemaining == 0) return;
  uint32_t now = millis();
  if (now - g_ackLastMs < ACK_HALF_PERIOD_MS) return;
  g_ackLastMs = now;
  g_ackOn = !g_ackOn;
  ledsSetDuty(g_ackOn ? dutyFromPercent(ACK_BRIGHTNESS_PERCENT) : 0);
  if (--g_ackRemaining == 0) {
    g_ledMode = LED_STANDBY;            // force a real transition next tick
    allLedsOff();
    ledUpdateMode();
  }
}

/* ==========================================================================
 *  STATE HANDLING
 * ========================================================================== */

/*
 * Records a new state byte and, optionally, notifies the host.
 *
 * This runs on the NimBLE host task, so it deliberately does NOT touch the
 * LEDs or the power manager: loop() calls ledUpdateMode() every tick and picks
 * the change up within one loop delay (<= LOOP_DELAY_STANDBY_MS, comfortably
 * inside the ~0.6 s already accepted in [A10]). Driving LEDC and
 * esp_pm_configure() from two tasks with no lock between them is a race whose
 * symptoms -- an occasional dropped frame of the breathing curve, or a
 * light-sleep flag that disagrees with the LED mode -- would be nearly
 * impossible to diagnose after the fact.
 *
 * ledUpdateMode() is idempotent (it returns early when the mode is unchanged),
 * so a heartbeat write of the same value still cannot restart the breathing
 * curve mid-fade.
 */
static void setState(uint8_t newState, bool notifyHost) {
  g_state = newState;

  if (notifyHost && g_stateChar != nullptr && g_connected) {
    g_stateChar->setValue(&newState, 1);
    g_stateChar->notify();
  }
  DBGF("[STATE] 0x%02X  cam=%d mic=%d  -> %s\n",
       newState,
       (newState & STATE_BIT_CAMERA) ? 1 : 0,
       (newState & STATE_BIT_MICROPHONE) ? 1 : 0,
       stateIsActive() ? "RED breathing" : "idle");
}

static void forceStateOff(const char* reason) {
  DBG("[FAILSAFE] back to idle: ");
  DBGLN(reason);
  setState(0x00, true);
  g_lastStateWriteMs = millis();
}

/* ==========================================================================
 *  BATTERY
 * ========================================================================== */

static uint16_t readBatteryMillivolts() {
  /* The 100K/100K divider presents a ~50K source, which is high for the ADC's
   * sample-and-hold: the first conversion after an idle period comes back low
   * while the internal cap charges. Throw it away. The 100 nF at the pin does
   * the rest -- without that capacitor no amount of oversampling fixes the
   * bias, it only averages it. */
  (void)analogReadMilliVolts(ADC_PIN);

  uint32_t acc = 0;
  for (uint8_t i = 0; i < BATTERY_SAMPLES; i++) {
    /* analogReadMilliVolts() applies the factory eFuse ADC calibration,
     * far more accurate than scaling the raw 12-bit count. */
    acc += analogReadMilliVolts(ADC_PIN);
  }
  uint32_t avgMv = acc / BATTERY_SAMPLES;
  return (uint16_t)(avgMv * BATTERY_DIVIDER_RATIO);
}

static uint8_t millivoltsToPercent(uint16_t mv) {     // [A4]
  if (mv >= BATTERY_MAX_MV) return 100;
  if (mv <= BATTERY_MIN_MV) return 0;
  return (uint8_t)(((uint32_t)(mv - BATTERY_MIN_MV) * 100UL) /
                    (BATTERY_MAX_MV - BATTERY_MIN_MV));
}

static void updateBattery(bool forceNotify) {
  uint16_t mv      = readBatteryMillivolts();
  uint8_t  percent = millivoltsToPercent(mv);

  bool changed = (percent != g_batteryPercent);
  g_batteryPercent = percent;

  if (g_batteryChar != nullptr) {
    g_batteryChar->setValue(&g_batteryPercent, 1);
    if (g_connected && (changed || forceNotify)) {
      g_batteryChar->notify();
    }
  }
  DBGF("[BATT] %u mV -> %u%%\n", mv, percent);
}

/* ==========================================================================
 *  BLE CALLBACKS
 * ========================================================================== */

class ServerCallbacks : public NimBLEServerCallbacks {
  void onConnect(NimBLEServer* pServer, NimBLEConnInfo& connInfo) override {
    g_connected = true;
    DBGF("[BLE] connected, handle=%u bonded=%d\n",
         connInfo.getConnHandle(), connInfo.isBonded() ? 1 : 0);

    /* [A10] Relaxed parameters: this link carries a byte every few minutes. */
    pServer->updateConnParams(connInfo.getConnHandle(),
                              CONN_MIN_INTERVAL, CONN_MAX_INTERVAL,
                              CONN_LATENCY, CONN_TIMEOUT);

#if SEARCH_OFF_ON_CONNECT
    /* The pulses mean "awake but not connected". Once the PC is on the line
     * they have served their purpose, so drop them early and start saving.
     * loop() performs the LED transition itself -- see setState(). */
    cancelSearchWindow();
#endif
  }

  void onDisconnect(NimBLEServer* pServer, NimBLEConnInfo& connInfo,
                    int reason) override {
    g_connected = false;
    DBGF("[BLE] disconnected, reason=%d\n", reason);

    /* FAILSAFE LAYER 1 -- unconditional. The host may have crashed, slept or
     * walked out of range without ever writing 0. */
    setState(0x00, false);
    g_lastStateWriteMs = millis();

    startSearchWindow("disconnect");  // loop() drives the LEDs, see setState()

    NimBLEDevice::startAdvertising();
    DBGLN("[BLE] advertising restarted");
  }

  void onAuthenticationComplete(NimBLEConnInfo& connInfo) override {
    DBGF("[BLE] auth complete: encrypted=%d bonded=%d\n",
         connInfo.isEncrypted() ? 1 : 0, connInfo.isBonded() ? 1 : 0);
    if (!connInfo.isEncrypted()) {
      DBGLN("[BLE] link not encrypted -> dropping");
      NimBLEDevice::getServer()->disconnect(connInfo.getConnHandle());
    }
  }
};

class StateCharCallbacks : public NimBLECharacteristicCallbacks {
  void onWrite(NimBLECharacteristic* pChar, NimBLEConnInfo& connInfo) override {
    /* [A6] Second layer of the bonding check. */
    if (!connInfo.isBonded()) {
      DBGLN("[BLE] write from non-bonded peer -> ignored");
      return;
    }

    NimBLEAttValue val = pChar->getValue();
    if (val.length() != 1) {
      DBGF("[BLE] bad write length %u -> ignored\n", val.length());
      return;
    }

    uint8_t incoming  = val[0];
    uint8_t sanitized = incoming & STATE_ACTIVE_MASK;
    if (sanitized != incoming) {
      DBGF("[BLE] reserved bits ignored (0x%02X -> 0x%02X)\n",
           incoming, sanitized);
    }

    g_lastStateWriteMs = millis();       // [A3] resets the 30 min failsafe
    setState(sanitized, true);
  }

  void onSubscribe(NimBLECharacteristic* pChar, NimBLEConnInfo& connInfo,
                   uint16_t subValue) override {
    DBGF("[BLE] state subscribe value=%u\n", subValue);
  }
};

static ServerCallbacks    g_serverCallbacks;
static StateCharCallbacks g_stateCallbacks;

/* ==========================================================================
 *  BUTTON  (GPIO9 / BOOT, active LOW, non-blocking, debounced)
 * ========================================================================== */

static void clearBondsAndRepair() {
  DBGLN("[BTN] clearing all bonds, re-entering pairing mode");

  if (g_connected && g_server != nullptr) {
    std::vector<uint16_t> peers = g_server->getPeerDevices();
    for (uint16_t handle : peers) {
      g_server->disconnect(handle);
    }
    /* disconnect() only queues the request. Dropping the bond table while a
     * link is still tearing down races the host task, so give it a moment. */
    delay(100);
  }

  NimBLEDevice::deleteAllBonds();
  setState(0x00, false);
  g_lastStateWriteMs = millis();

  NimBLEDevice::startAdvertising();
  ledStartAck(3);
}

static void serviceButton() {
  uint32_t now = millis();
  bool raw = (digitalRead(BUTTON_PIN) == LOW);   // active LOW

  if (raw != g_btnLastRaw) {
    g_btnLastRaw  = raw;
    g_btnChangeMs = now;
    return;
  }

  if (now - g_btnChangeMs < BUTTON_DEBOUNCE_MS) return;

  if (raw && !g_btnStableLow) {
    g_btnStableLow   = true;
    g_btnPressStart  = now;
    g_btnActionFired = false;
    DBGLN("[BTN] pressed");
  } else if (!raw && g_btnStableLow) {
    g_btnStableLow = false;
    DBGF("[BTN] released after %lu ms\n", (unsigned long)(now - g_btnPressStart));
  }

  if (g_btnStableLow && !g_btnActionFired &&
      (now - g_btnPressStart >= BUTTON_HOLD_MS)) {
    g_btnActionFired = true;
    clearBondsAndRepair();
  }
}

/* ==========================================================================
 *  SETUP
 * ========================================================================== */

static void setupBLE() {
  NimBLEDevice::init(BLE_DEVICE_NAME);

  DBGF("[BLE] MAC Adresi: %s\n", NimBLEDevice::getAddress().toString().c_str());

  NimBLEDevice::setPower(BLE_TX_POWER);          // [A9] dBm, not an enum

  /* JustWorks bonding: bond=true, MITM=false, secure connections=true. */
  NimBLEDevice::setSecurityAuth(true, false, true);
  NimBLEDevice::setSecurityIOCap(BLE_HS_IO_NO_INPUT_OUTPUT);

  g_server = NimBLEDevice::createServer();
  g_server->setCallbacks(&g_serverCallbacks);

  /* ---- Custom On Air service ---- */
  NimBLEService* onAirService = g_server->createService(ONAIR_SERVICE_UUID);
  g_stateChar = onAirService->createCharacteristic(
      ONAIR_STATE_CHAR_UUID,
      NIMBLE_PROPERTY::WRITE  |
      NIMBLE_PROPERTY::NOTIFY |
      NIMBLE_PROPERTY::READ_ENC |
      NIMBLE_PROPERTY::WRITE_ENC);
  g_stateChar->setCallbacks(&g_stateCallbacks);
  uint8_t initial = 0x00;
  g_stateChar->setValue(&initial, 1);
  /* No service->start() here: in NimBLE 2.x it is a no-op kept only for source
   * compatibility. Services go live when the server starts advertising. */

  /* ---- Standard Battery Service (0x180F) ---- */
  NimBLEService* batteryService = g_server->createService(BATTERY_SERVICE_UUID);
  g_batteryChar = batteryService->createCharacteristic(
      BATTERY_LEVEL_CHAR_UUID,
      NIMBLE_PROPERTY::READ | NIMBLE_PROPERTY::NOTIFY);
  g_batteryChar->setValue(&g_batteryPercent, 1);

  /* ---- Advertising ---- [A7] */
  NimBLEAdvertising* adv = NimBLEDevice::getAdvertising();

  NimBLEAdvertisementData advData;
  advData.setFlags(BLE_HS_ADV_F_DISC_GEN | BLE_HS_ADV_F_BREDR_UNSUP);
  advData.addServiceUUID(NimBLEUUID(ONAIR_SERVICE_UUID));

  NimBLEAdvertisementData scanData;
  scanData.setName(BLE_DEVICE_NAME);

  adv->setAdvertisementData(advData);
  adv->setScanResponseData(scanData);
  adv->enableScanResponse(true);

  const uint16_t advUnits = (uint16_t)((ADVERTISING_INTERVAL_MS * 1000UL) / 625UL);
  adv->setMinInterval(advUnits);
  adv->setMaxInterval(advUnits + 80);

  NimBLEDevice::startAdvertising();
  DBGLN("[BLE] advertising started as \"" BLE_DEVICE_NAME "\"");
}

void setup() {
  /* Before anything else: the default is 160 MHz, and halving the clock roughly
   * halves the CPU's share of the current draw. Unlike light sleep this works
   * on every build, PM or no PM. Arduino re-derives the UART dividers, so it is
   * safe either side of Serial.begin(). */
  setCpuFrequencyMhz(CPU_MAX_FREQ_MHZ);

  DBG_BEGIN();

#ifdef DEBUG_SERIAL
  /* Dahili USB CDC baglantisinin Seri Monitor ile senkronize olmasini bekle.
   * Guarded, so a production build with DEBUG_SERIAL off does not spend 3 s of
   * every boot waiting for a port nobody is listening to. */
  unsigned long start = millis();
  while (!Serial && (millis() - start < 3000)) {
    delay(10);
  }
#endif

  DBGLN("\n=== On Air BLE indicator booting ===");

  ledInit();
  pinMode(BUTTON_PIN, INPUT_PULLUP);

  analogReadResolution(12);
  analogSetPinAttenuation(ADC_PIN, ADC_11db);

  g_lastStateWriteMs  = millis();
  g_lastBatteryReadMs = millis();

  updateBattery(false);
  setupBLE();

  /* Pulses announce that the device is awake and looking for the PC. */
  startSearchWindow("boot");
  g_ledMode = LED_STANDBY;               // force a real transition
  ledUpdateMode();

  DBGF("[LED] %u LEDs; search %u%% pulses for %lu s, then dark; "
       "breathing cycle %lu ms\n",
       (unsigned)LED_COUNT,
       (unsigned)SEARCH_BRIGHTNESS_PERCENT,
       (unsigned long)(SEARCH_WINDOW_MS / 1000UL),
       (unsigned long)BREATH_PERIOD_MS);
  DBGLN("[SYS] ready");
}

/* ==========================================================================
 *  MAIN LOOP
 *
 *  Ends in delay() on purpose. On ESP32 Arduino delay() is vTaskDelay, which
 *  yields to the FreeRTOS idle task and lets tickless idle drop the chip into
 *  light sleep. A loop that merely polls millis() and returns would spin the
 *  CPU at 100% and make esp_pm_configure() pointless.
 * ========================================================================== */

void loop() {
  serviceButton();
  serviceAck();
  serviceBreathing();
  serviceSearching();
  ledUpdateMode();                       // also expires the search window

  uint32_t now = millis();

  /* ---- FAILSAFE LAYER 2: inactivity timeout ---- */
  if (stateIsActive() &&
      (uint32_t)(now - g_lastStateWriteMs) >= TIMEOUT_MS) {
    forceStateOff("no state write for TIMEOUT_MS");
  }

  /* ---- Periodic battery measurement ---- */
  if ((uint32_t)(now - g_lastBatteryReadMs) >= BATTERY_READ_INTERVAL_MS) {
    g_lastBatteryReadMs = now;
    updateBattery(false);
  }

  /* Long naps when nothing is animating, short ones while a PWM curve runs. */
  delay(g_ledMode == LED_STANDBY ? LOOP_DELAY_STANDBY_MS
                                 : LOOP_DELAY_ANIMATING_MS);
}
