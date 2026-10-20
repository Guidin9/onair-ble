/*
 * ============================================================================
 *  LED Breathing Test  --  On Air hardware bring-up sketch
 *  Target : ESP32-C3 SuperMini
 *  Board  : "ESP32C3 Dev Module",  USB CDC On Boot: Enabled
 * ============================================================================
 *
 *  PURPOSE
 *    Standalone LED test. No BLE, no battery reading, no bonding -- just the
 *    breathing curve on all four LED pins, so wiring problems can be found
 *    before the real firmware is loaded.
 *
 *  WIRING  (8 red LEDs, 2 per pin, ONE 330R resistor per LED)
 *    GPIO5  -> 330R -> LED -> GND    (x2 in parallel)
 *    GPIO6  -> 330R -> LED -> GND    (x2 in parallel)
 *    GPIO7  -> 330R -> LED -> GND    (x2 in parallel)
 *    GPIO10 -> 330R -> LED -> GND    (x2 in parallel)
 *
 *    Every LED needs its own resistor. Sharing one resistor between parallel
 *    LEDs lets the lowest-forward-voltage part take most of the current: it
 *    runs brighter than its neighbour and burns out early.
 *    LED long leg (anode) towards the GPIO, short leg / flat side to GND.
 *
 *  WHAT YOU SHOULD SEE
 *    All 8 LEDs fading smoothly up and down together, one full cycle every
 *    3 seconds, with no visible stepping or flicker.
 *
 *  TEST MODES  --  set TEST_MODE below
 *    0 = all pins breathe together        (normal appearance test)
 *    1 = pins breathe one after another   (find a miswired pin)
 *    2 = all pins full on, no PWM         (check every LED lights at all)
 * ============================================================================
 */

#include <math.h>

/* ==========================================================================
 *  CONFIGURATION
 * ========================================================================== */

#define TEST_MODE                0       // 0 = together, 1 = sequential, 2 = static on

#define LED_PIN_1                5
#define LED_PIN_2                6
#define LED_PIN_3                7
#define LED_PIN_4                10

#define BREATH_PERIOD_MS         3000UL  // one full fade in + fade out
#define BREATH_UPDATE_MS         20UL    // 50 Hz refresh, smooth to the eye
#define MAX_BRIGHTNESS_PERCENT   100     // lower this if the LEDs are too bright

#define LEDC_FREQ_HZ             1000    // far above the flicker fusion threshold
#define LEDC_RESOLUTION_BITS     8       // duty range 0..255

#define SEQUENTIAL_STAGGER_MS    750UL   // phase offset per pin in TEST_MODE 1

#define SERIAL_BAUD              115200

/* ==========================================================================
 *  INTERNALS
 * ========================================================================== */

static const uint8_t LED_PINS[] = { LED_PIN_1, LED_PIN_2, LED_PIN_3, LED_PIN_4 };
static const uint8_t LED_COUNT  = sizeof(LED_PINS) / sizeof(LED_PINS[0]);

static uint32_t g_startMs      = 0;
static uint32_t g_lastUpdateMs = 0;
static uint32_t g_lastLogMs    = 0;

/* Converts 0..100 into the 8-bit LEDC duty range. */
static uint16_t dutyFromPercent(uint32_t percent) {
  uint32_t maxDuty = (1UL << LEDC_RESOLUTION_BITS) - 1UL;
  if (percent > 100) percent = 100;
  return (uint16_t)((maxDuty * percent) / 100UL);
}

/*
 * Breathing curve for a given elapsed time.
 *
 * A raised cosine gives a smooth 0 -> 1 -> 0 sweep with no hard edges at the
 * turning points. Squaring it applies a rough gamma correction: the eye
 * perceives brightness close to logarithmically, so a linear duty ramp looks
 * like it snaps on and then lingers at full. Squaring restores an even fade.
 */
static uint16_t breathDuty(uint32_t elapsedMs) {
  uint32_t pos   = elapsedMs % BREATH_PERIOD_MS;
  float    phase = (float)pos / (float)BREATH_PERIOD_MS;

  float linear    = (1.0f - cosf(2.0f * (float)M_PI * phase)) * 0.5f;
  float perceived = linear * linear;

  uint16_t peak = dutyFromPercent(MAX_BRIGHTNESS_PERCENT);
  return (uint16_t)(perceived * (float)peak);
}

/* ==========================================================================
 *  SETUP
 * ========================================================================== */

void setup() {
  Serial.begin(SERIAL_BAUD);

  /* Give the native USB CDC link a moment to come up, but never block on it
   * -- the sketch has to run standalone on battery too. */
  uint32_t waitStart = millis();
  while (!Serial && (millis() - waitStart < 3000)) {
    delay(10);
  }

  Serial.println("\n=== LED breathing test ===");

  for (uint8_t i = 0; i < LED_COUNT; i++) {
    /* ledcAttach() is the ESP32 core 3.x API.
     * On core 2.x this would be ledcSetup() + ledcAttachPin(). */
    ledcAttach(LED_PINS[i], LEDC_FREQ_HZ, LEDC_RESOLUTION_BITS);
    ledcWrite(LED_PINS[i], 0);
    Serial.printf("  GPIO%-2u attached\n", LED_PINS[i]);
  }

#if TEST_MODE == 0
  Serial.printf("Mode 0: all pins breathing together, %lu ms per cycle\n",
                (unsigned long)BREATH_PERIOD_MS);
#elif TEST_MODE == 1
  Serial.printf("Mode 1: sequential, %lu ms stagger between pins\n",
                (unsigned long)SEQUENTIAL_STAGGER_MS);
#else
  Serial.println("Mode 2: all pins static full on");
#endif

  g_startMs = millis();
}

/* ==========================================================================
 *  MAIN LOOP
 *
 *  delay() at the end is deliberate: on ESP32 Arduino it maps to vTaskDelay,
 *  which yields to the scheduler instead of spinning the CPU. Same pattern the
 *  real firmware uses so power behaviour matches.
 * ========================================================================== */

void loop() {
  uint32_t now = millis();

#if TEST_MODE == 2

  /* Static full brightness -- confirms every LED lights and no LED is dead,
   * reversed, or shorted. PWM is bypassed entirely. */
  uint16_t duty = dutyFromPercent(MAX_BRIGHTNESS_PERCENT);
  for (uint8_t i = 0; i < LED_COUNT; i++) {
    ledcWrite(LED_PINS[i], duty);
  }
  delay(100);

#else

  if (now - g_lastUpdateMs >= BREATH_UPDATE_MS) {
    g_lastUpdateMs = now;
    uint32_t elapsed = now - g_startMs;

    for (uint8_t i = 0; i < LED_COUNT; i++) {
  #if TEST_MODE == 1
      /* Offset each pin in time so they chase one another. Any pin that stays
       * dark or lights out of turn points straight at a wiring fault. */
      uint32_t offset = (uint32_t)i * SEQUENTIAL_STAGGER_MS;
      ledcWrite(LED_PINS[i], breathDuty(elapsed + offset));
  #else
      ledcWrite(LED_PINS[i], breathDuty(elapsed));
  #endif
    }
  }

  /* Heartbeat print so a frozen sketch is obvious on the serial monitor. */
  if (now - g_lastLogMs >= 5000UL) {
    g_lastLogMs = now;
    Serial.printf("[%lu s] running\n", (unsigned long)((now - g_startMs) / 1000UL));
  }

  delay(5);

#endif
}
