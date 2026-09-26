// ============================================================================
//  wk_hid  —  firmware for the bot's Hardware input mode
//  Target: Arduino Pro Micro / Leonardo (ATmega32U4, native USB)
//
//  The board enumerates as a composite USB device: a CDC serial port (which the
//  bot talks to) plus a HID keyboard and a HID mouse (which Windows sees as
//  ordinary input hardware). Nothing the bot does reaches Windows through an
//  API — the keystrokes and cursor movement arrive over USB from a device that,
//  from the operating system's side of the wire, is a keyboard and a mouse.
//  That is the whole point of the mode: SendInput sets LLMHF_INJECTED on every
//  event it produces and any process may read that flag; a HID report carries
//  no such thing.
//
//  ── Why the mouse reports ABSOLUTE coordinates ─────────────────────────────
//  A real mouse reports deltas, and Windows then puts them through the pointer
//  acceleration curve, so a delta of 10 does not move the cursor 10 pixels. The
//  bot aims at exact screen coordinates (a battle-list row is 14 px tall), so a
//  relative device would need a read-back-and-correct loop on every frame of
//  every movement, at the mercy of a setting in the user's control panel.
//
//  An absolute pointer sidesteps all of it: the report says WHERE, the cursor
//  goes there, and acceleration does not apply to absolute input at all. The PC
//  side still calibrates the logical range against the real desktop rather than
//  assuming it (see ArduinoLink.Calibrate) because how Windows maps that range
//  depends on the monitor layout.
//
//  The human motion is unchanged and still lives on the PC: InputManager walks
//  a Bezier path against a clock and sends a position every ~8 ms. This firmware
//  is deliberately dumb — it holds no timing policy of its own, because timing
//  is what makes the input look human and it is already solved up there.
//
//  ── And a second, RELATIVE pointer, for clicking where the cursor is ───────
//  An absolute report cannot say "wherever the cursor is now": every report
//  carries a position, so a button pressed through it lands on — and moves the
//  cursor to — the last place the board was told. Clicking at the user's own
//  cursor through it meant reading the cursor and sending it back first, which
//  put every click at the mercy of the calibration and dragged the pointer out
//  from under a hand that was moving it.
//
//  Report 3 is an ordinary relative mouse that never moves: its reports carry
//  a button and zero movement, which is exactly what a physical mouse sends when
//  it is clicked in place. Windows applies it at the cursor, wherever that is.
//  (Version 2 of the protocol. wk, which only speaks version 1, still works:
//  nothing it uses changed.)
//
//  ── Protocol ───────────────────────────────────────────────────────────────
//  Line based ASCII, one command per line, '\n' terminated, one reply per
//  command. Text rather than binary so a plain serial monitor is enough to
//  debug the board with the bot closed.
//
//    P                 ping / identify        -> WKHID 2
//    KD <usage>        key down, HID usage id -> OK
//    KU <usage>        key up                 -> OK
//    KR                release every key      -> OK
//    MM <x> <y>        move, 0..32767 both    -> OK
//    MD <button>       1=left 2=right 3=mid   -> OK
//    MU <button>       button up              -> OK
//    CD <button>       button down IN PLACE   -> OK   (relative pointer, v2)
//    CU <button>       button up in place     -> OK   (relative pointer, v2)
//    MW <delta>        wheel, -127..127       -> OK
//    MR                release every button   -> OK
//    R                 release EVERYTHING     -> OK
//    H                 heartbeat (no-op)      -> OK
//    S                 state, for diagnostics -> ST <mods> <keys> <btn> <x> <y>
//
//  Anything else answers ERR <reason>. The reply is what gives the PC side flow
//  control: it never has more than one command outstanding, so the 64-byte CDC
//  receive buffer cannot overrun however fast the bot decides to move.
//
//  ── Safety ────────────────────────────────────────────────────────────────
//  A key left down walks the character away with nothing watching, and a mouse
//  button left down turns the next click into a drag. Two independent releases
//  guard against that, because the PC side cannot be trusted to always get its
//  Finally block in:
//
//    * the watchdog below releases everything if nothing arrives for
//      WATCHDOG_MS while something is held — that covers the bot crashing, the
//      cable being pulled at the PC end, or the machine sleeping;
//    * losing DTR (the port being closed, the app exiting) releases everything
//      immediately.
//
//  While something is held the bot sends H every ~700 ms precisely so a long
//  deliberate hold — walking a straight run holds one arrow key for seconds —
//  is never mistaken for an abandoned one.
// ============================================================================

#include <HID.h>

// ── Report descriptor ───────────────────────────────────────────────────────
// Two top-level collections behind one interface, told apart by report id.
static const uint8_t WK_REPORT_DESCRIPTOR[] PROGMEM = {

    // ---- Report 1: keyboard (boot-style: modifier byte + 6 key slots) ----
    0x05, 0x01,       // Usage Page (Generic Desktop)
    0x09, 0x06,       // Usage (Keyboard)
    0xA1, 0x01,       // Collection (Application)
    0x85, 0x01,       //   Report ID (1)
    0x05, 0x07,       //   Usage Page (Keyboard/Keypad)
    0x19, 0xE0,       //   Usage Minimum (Left Control)
    0x29, 0xE7,       //   Usage Maximum (Right GUI)
    0x15, 0x00,       //   Logical Minimum (0)
    0x25, 0x01,       //   Logical Maximum (1)
    0x75, 0x01,       //   Report Size (1)
    0x95, 0x08,       //   Report Count (8)
    0x81, 0x02,       //   Input (Data, Variable, Absolute)  <- modifier bits
    0x95, 0x01,       //   Report Count (1)
    0x75, 0x08,       //   Report Size (8)
    0x81, 0x03,       //   Input (Constant)                  <- reserved byte
    0x95, 0x06,       //   Report Count (6)
    0x75, 0x08,       //   Report Size (8)
    0x15, 0x00,       //   Logical Minimum (0)
    0x25, 0xA4,       //   Logical Maximum (164)
    0x05, 0x07,       //   Usage Page (Keyboard/Keypad)
    0x19, 0x00,       //   Usage Minimum (0)
    0x29, 0xA4,       //   Usage Maximum (164)
    0x81, 0x00,       //   Input (Data, Array)               <- 6 pressed keys
    0xC0,             // End Collection

    // ---- Report 2: mouse, absolute X/Y ----
    0x05, 0x01,       // Usage Page (Generic Desktop)
    0x09, 0x02,       // Usage (Mouse)
    0xA1, 0x01,       // Collection (Application)
    0x85, 0x02,       //   Report ID (2)
    0x09, 0x01,       //   Usage (Pointer)
    0xA1, 0x00,       //   Collection (Physical)
    0x05, 0x09,       //     Usage Page (Button)
    0x19, 0x01,       //     Usage Minimum (Button 1)
    0x29, 0x05,       //     Usage Maximum (Button 5)
    0x15, 0x00,       //     Logical Minimum (0)
    0x25, 0x01,       //     Logical Maximum (1)
    0x95, 0x05,       //     Report Count (5)
    0x75, 0x01,       //     Report Size (1)
    0x81, 0x02,       //     Input (Data, Variable, Absolute)
    0x95, 0x01,       //     Report Count (1)
    0x75, 0x03,       //     Report Size (3)
    0x81, 0x03,       //     Input (Constant)                <- pad to a byte
    0x05, 0x01,       //     Usage Page (Generic Desktop)
    0x09, 0x30,       //     Usage (X)
    0x09, 0x31,       //     Usage (Y)
    0x16, 0x00, 0x00, //     Logical Minimum (0)
    0x26, 0xFF, 0x7F, //     Logical Maximum (32767)
    0x75, 0x10,       //     Report Size (16)
    0x95, 0x02,       //     Report Count (2)
    0x81, 0x02,       //     Input (Data, Variable, ABSOLUTE)
    0x09, 0x38,       //     Usage (Wheel)
    0x15, 0x81,       //     Logical Minimum (-127)
    0x25, 0x7F,       //     Logical Maximum (127)
    0x75, 0x08,       //     Report Size (8)
    0x95, 0x01,       //     Report Count (1)
    0x81, 0x06,       //     Input (Data, Variable, Relative)
    0xC0,             //   End Collection
    0xC0,             // End Collection

    // ---- Report 3: mouse, relative, for clicks in place ----
    // X and Y are declared because Windows only treats a collection as a mouse
    // when it has them; the firmware always sends them as zero.
    0x05, 0x01,       // Usage Page (Generic Desktop)
    0x09, 0x02,       // Usage (Mouse)
    0xA1, 0x01,       // Collection (Application)
    0x85, 0x03,       //   Report ID (3)
    0x09, 0x01,       //   Usage (Pointer)
    0xA1, 0x00,       //   Collection (Physical)
    0x05, 0x09,       //     Usage Page (Button)
    0x19, 0x01,       //     Usage Minimum (Button 1)
    0x29, 0x05,       //     Usage Maximum (Button 5)
    0x15, 0x00,       //     Logical Minimum (0)
    0x25, 0x01,       //     Logical Maximum (1)
    0x95, 0x05,       //     Report Count (5)
    0x75, 0x01,       //     Report Size (1)
    0x81, 0x02,       //     Input (Data, Variable, Absolute)
    0x95, 0x01,       //     Report Count (1)
    0x75, 0x03,       //     Report Size (3)
    0x81, 0x03,       //     Input (Constant)                <- pad to a byte
    0x05, 0x01,       //     Usage Page (Generic Desktop)
    0x09, 0x30,       //     Usage (X)
    0x09, 0x31,       //     Usage (Y)
    0x15, 0x81,       //     Logical Minimum (-127)
    0x25, 0x7F,       //     Logical Maximum (127)
    0x75, 0x08,       //     Report Size (8)
    0x95, 0x02,       //     Report Count (2)
    0x81, 0x06,       //     Input (Data, Variable, RELATIVE)
    0xC0,             //   End Collection
    0xC0              // End Collection
};

static const uint8_t REPORT_ID_KEYBOARD = 1;
static const uint8_t REPORT_ID_MOUSE    = 2;
static const uint8_t REPORT_ID_CURSOR   = 3;

// Six is what the boot keyboard report holds, and it is far more than the bot
// needs — modifiers do not occupy a slot, so "Ctrl held while two arrows tap"
// costs two.
static const uint8_t KEY_SLOTS = 6;

// Usage ids of the eight modifiers, in the bit order the modifier byte wants.
static const uint8_t MOD_USAGE_FIRST = 0xE0;
static const uint8_t MOD_USAGE_LAST  = 0xE7;

// How long the board will hold anything without hearing from the PC. Longer
// than the heartbeat by a wide margin (700 ms), short enough that a crash does
// not leave the character walking.
static const unsigned long WATCHDOG_MS = 2500UL;

static const uint8_t  CMD_BUFFER_SIZE = 48;

// ── Live device state ───────────────────────────────────────────────────────
// Mirrored here rather than re-derived per report: a HID report is the WHOLE
// state of the device every time, not a change to it, so the board has to know
// what is currently held in order to say anything at all.
static uint8_t  keyModifiers = 0;
static uint8_t  keySlots[KEY_SLOTS] = {0, 0, 0, 0, 0, 0};
static uint8_t  mouseButtons = 0;
static uint16_t mouseX = 16383;          // centre of the range until told otherwise
static uint16_t mouseY = 16383;
static uint8_t  cursorButtons = 0;       // held on the relative pointer (report 3)

static char          cmdBuffer[CMD_BUFFER_SIZE];
static uint8_t       cmdLength = 0;
static bool          cmdOverflow = false;
static unsigned long lastCommandMs = 0;
static bool          wasConnected = false;

// ── Report senders ──────────────────────────────────────────────────────────

static void sendKeyboardReport() {
    uint8_t report[2 + KEY_SLOTS];
    report[0] = keyModifiers;
    report[1] = 0;                        // reserved, always zero
    for (uint8_t i = 0; i < KEY_SLOTS; i++) report[2 + i] = keySlots[i];
    HID().SendReport(REPORT_ID_KEYBOARD, report, sizeof(report));
}

// The wheel is the one relative field in the report, so it is passed per call
// and never stored: a report sent for a click must carry wheel = 0 or the page
// would scroll again with every button event.
static void sendMouseReport(int8_t wheel) {
    uint16_t x = mouseX;

    // A report whose every byte is zero never reaches the cursor. With no button
    // down, no wheel movement and both axes at zero there is nothing in it to
    // tell apart from "nothing changed", and Windows' mouse stack drops it.
    //
    // Measured on 2026-08-31, not theorised: "MM 0 0" left the cursor exactly
    // where it stood, while "MM 0 32767" put X on the left edge and Y at the
    // bottom in the same breath — so it is the all-zero report, not the value 0
    // on an axis.
    //
    // Nudging X to 1 costs nothing real: one logical unit is far finer than a
    // pixel (32767 of them across a 1920-px screen is about 17 per pixel), so
    // logical 1 lands on screen pixel 0 — the pixel that was asked for.
    if (mouseButtons == 0 && x == 0 && mouseY == 0 && wheel == 0) x = 1;

    uint8_t report[6];
    report[0] = mouseButtons;
    report[1] = (uint8_t)(x & 0xFF);
    report[2] = (uint8_t)(x >> 8);
    report[3] = (uint8_t)(mouseY & 0xFF);
    report[4] = (uint8_t)(mouseY >> 8);
    report[5] = (uint8_t)wheel;
    HID().SendReport(REPORT_ID_MOUSE, report, sizeof(report));
}

// The relative pointer: its buttons and no movement, ever. An all-zero report
// here is a button coming up — every physical mouse sends exactly that — so it
// needs none of the absolute report's care above.
static void sendCursorReport() {
    uint8_t report[3];
    report[0] = cursorButtons;
    report[1] = 0;                        // X: stays where the cursor is
    report[2] = 0;                        // Y: likewise
    HID().SendReport(REPORT_ID_CURSOR, report, sizeof(report));
}

// ── Keyboard state ──────────────────────────────────────────────────────────

static bool isModifierUsage(uint8_t usage) {
    return usage >= MOD_USAGE_FIRST && usage <= MOD_USAGE_LAST;
}

// True when the key went down, false when there was no room for it. A full
// report is not an error the caller can do anything about, but it must not be
// reported as a press that happened.
static bool pressKey(uint8_t usage) {
    if (usage == 0) return false;

    if (isModifierUsage(usage)) {
        keyModifiers |= (uint8_t)(1 << (usage - MOD_USAGE_FIRST));
        sendKeyboardReport();
        return true;
    }

    for (uint8_t i = 0; i < KEY_SLOTS; i++) {
        if (keySlots[i] == usage) {        // already down: the report is correct
            sendKeyboardReport();          // resend anyway, as auto-repeat does
            return true;
        }
    }
    for (uint8_t i = 0; i < KEY_SLOTS; i++) {
        if (keySlots[i] == 0) {
            keySlots[i] = usage;
            sendKeyboardReport();
            return true;
        }
    }
    return false;
}

static void releaseKey(uint8_t usage) {
    if (isModifierUsage(usage)) {
        keyModifiers &= (uint8_t)~(1 << (usage - MOD_USAGE_FIRST));
    } else {
        for (uint8_t i = 0; i < KEY_SLOTS; i++) {
            if (keySlots[i] == usage) keySlots[i] = 0;
        }
    }
    sendKeyboardReport();
}

static void releaseAllKeys() {
    keyModifiers = 0;
    for (uint8_t i = 0; i < KEY_SLOTS; i++) keySlots[i] = 0;
    sendKeyboardReport();
}

static bool anyKeyHeld() {
    if (keyModifiers != 0) return true;
    for (uint8_t i = 0; i < KEY_SLOTS; i++) {
        if (keySlots[i] != 0) return true;
    }
    return false;
}

// ── Mouse state ─────────────────────────────────────────────────────────────

static void releaseAllButtons() {
    mouseButtons = 0;
    sendMouseReport(0);
    cursorButtons = 0;
    sendCursorReport();
}

static void releaseEverything() {
    releaseAllKeys();
    releaseAllButtons();
}

// ── Command parsing ─────────────────────────────────────────────────────────

// Reads a non-negative decimal number, or -1 when the text is not one. Written
// out rather than reached for through atoi/strtol so that trailing rubbish is
// rejected instead of silently ignored — "MM 100 abc" must be an error, not a
// move to (100, 0).
static long parseNumber(const char *text, bool allowNegative) {
    if (text == NULL || *text == '\0') return -1;

    bool negative = false;
    if (*text == '-') {
        if (!allowNegative) return -1;
        negative = true;
        text++;
        if (*text == '\0') return -1;
    }

    long value = 0;
    while (*text != '\0') {
        if (*text < '0' || *text > '9') return -1;
        value = value * 10 + (*text - '0');
        if (value > 1000000L) return -1;         // nothing here is ever this big
        text++;
    }
    return negative ? -value : value;
}

// Splits off the next whitespace-delimited token, advancing *cursor past it.
static char *nextToken(char **cursor) {
    char *p = *cursor;
    while (*p == ' ' || *p == '\t') p++;
    if (*p == '\0') { *cursor = p; return NULL; }

    char *start = p;
    while (*p != '\0' && *p != ' ' && *p != '\t') p++;
    if (*p != '\0') { *p = '\0'; p++; }
    *cursor = p;
    return start;
}

static void replyOk()                 { Serial.println(F("OK")); }
static void replyErr(const char *why) { Serial.print(F("ERR ")); Serial.println(why); }

static void handleCommand(char *line) {
    char *cursor = line;
    char *op = nextToken(&cursor);
    if (op == NULL) return;                    // blank line: not a command, not an error

    // ---- P: identify. The bot opens every COM port in turn and keeps the one
    // that answers this, so the reply has to be unmistakable.
    if (strcmp(op, "P") == 0) {
        Serial.println(F("WKHID 2"));
        return;
    }

    // ---- H: heartbeat. Does nothing except reset the watchdog, which the
    // caller already did by getting here.
    if (strcmp(op, "H") == 0) { replyOk(); return; }

    // ---- KD / KU: one key by HID usage id.
    if (strcmp(op, "KD") == 0 || strcmp(op, "KU") == 0) {
        long usage = parseNumber(nextToken(&cursor), false);

        // TWO ranges, and the second one is not optional. Ordinary keys live in
        // 1..0xA4, which is what the report's 6-key array declares. The eight
        // MODIFIERS are 0xE0..0xE7 and never enter that array at all — they are
        // bits in the report's first byte, so the array's logical maximum has
        // nothing to say about them.
        //
        // Checking only the first range compiled, ran, and answered "ERR usage"
        // to every Ctrl, Shift and Alt the bot has: which is most of it. Caught
        // on the bench on 2026-08-31 by pressing usage 224 and asking Windows
        // whether Ctrl was down.
        bool known = (usage >= 1 && usage <= 0xA4) ||
                     (usage >= MOD_USAGE_FIRST && usage <= MOD_USAGE_LAST);
        if (!known) { replyErr("usage"); return; }
        if (nextToken(&cursor) != NULL) { replyErr("extra"); return; }

        if (op[1] == 'D') {
            if (!pressKey((uint8_t)usage)) { replyErr("full"); return; }
        } else {
            releaseKey((uint8_t)usage);
        }
        replyOk();
        return;
    }

    if (strcmp(op, "KR") == 0) { releaseAllKeys(); replyOk(); return; }

    // ---- MM: absolute move.
    if (strcmp(op, "MM") == 0) {
        long x = parseNumber(nextToken(&cursor), false);
        long y = parseNumber(nextToken(&cursor), false);
        if (x < 0 || x > 32767 || y < 0 || y > 32767) { replyErr("range"); return; }
        if (nextToken(&cursor) != NULL) { replyErr("extra"); return; }

        mouseX = (uint16_t)x;
        mouseY = (uint16_t)y;
        sendMouseReport(0);
        replyOk();
        return;
    }

    // ---- MD / MU: one mouse button.
    if (strcmp(op, "MD") == 0 || strcmp(op, "MU") == 0) {
        long button = parseNumber(nextToken(&cursor), false);
        if (button < 1 || button > 5) { replyErr("button"); return; }
        if (nextToken(&cursor) != NULL) { replyErr("extra"); return; }

        uint8_t bit = (uint8_t)(1 << (button - 1));
        if (op[1] == 'D') mouseButtons |= bit;
        else              mouseButtons &= (uint8_t)~bit;
        sendMouseReport(0);
        replyOk();
        return;
    }

    // ---- CD / CU: one button on the relative pointer — a click where the
    // cursor already is, which does not move it. See the header.
    if (strcmp(op, "CD") == 0 || strcmp(op, "CU") == 0) {
        long button = parseNumber(nextToken(&cursor), false);
        if (button < 1 || button > 5) { replyErr("button"); return; }
        if (nextToken(&cursor) != NULL) { replyErr("extra"); return; }

        uint8_t bit = (uint8_t)(1 << (button - 1));
        if (op[1] == 'D') cursorButtons |= bit;
        else              cursorButtons &= (uint8_t)~bit;
        sendCursorReport();
        replyOk();
        return;
    }

    if (strcmp(op, "MR") == 0) { releaseAllButtons(); replyOk(); return; }

    // ---- MW: wheel, in detents.
    if (strcmp(op, "MW") == 0) {
        long delta = parseNumber(nextToken(&cursor), true);
        if (delta < -127 || delta > 127 || delta == 0) { replyErr("delta"); return; }
        if (nextToken(&cursor) != NULL) { replyErr("extra"); return; }

        sendMouseReport((int8_t)delta);
        replyOk();
        return;
    }

    // ---- R: the panic release. Deliberately one character so it can be typed
    // into a serial monitor in a hurry.
    if (strcmp(op, "R") == 0) { releaseEverything(); replyOk(); return; }

    // ---- S: what the board believes is held, for the Settings-tab self test.
    if (strcmp(op, "S") == 0) {
        Serial.print(F("ST "));
        Serial.print(keyModifiers);
        Serial.print(' ');
        uint8_t held = 0;
        for (uint8_t i = 0; i < KEY_SLOTS; i++) {
            if (keySlots[i] != 0) held++;
        }
        Serial.print(held);
        Serial.print(' ');
        Serial.print(mouseButtons | cursorButtons);
        Serial.print(' ');
        Serial.print(mouseX);
        Serial.print(' ');
        Serial.println(mouseY);
        return;
    }

    replyErr("opcode");
}

// ── Arduino entry points ────────────────────────────────────────────────────

void setup() {
    static HIDSubDescriptor node(WK_REPORT_DESCRIPTOR, sizeof(WK_REPORT_DESCRIPTOR));
    HID().AppendDescriptor(&node);

    // The baud rate is decoration on a 32U4: CDC runs at USB speed whatever
    // either side asks for. Kept at a conventional value so a serial monitor
    // opened by hand does not look wrong.
    Serial.begin(115200);

    // Start from a known-empty device rather than from whatever a reset left in
    // the host's view of it.
    releaseEverything();
    lastCommandMs = millis();
}

void loop() {
    // A closed port means the bot is gone — exiting, crashed, or switched away
    // from Hardware mode. Anything still held is now held by nobody.
    bool connected = (bool)Serial;
    if (wasConnected && !connected) releaseEverything();
    wasConnected = connected;

    while (Serial.available() > 0) {
        char c = (char)Serial.read();

        if (c == '\r') continue;
        if (c == '\n') {
            if (cmdOverflow) {
                replyErr("toolong");
            } else {
                cmdBuffer[cmdLength] = '\0';
                lastCommandMs = millis();
                handleCommand(cmdBuffer);
            }
            cmdLength = 0;
            cmdOverflow = false;
            continue;
        }

        if (cmdLength < CMD_BUFFER_SIZE - 1) {
            cmdBuffer[cmdLength++] = c;
        } else {
            // Keep consuming to the newline so the next command starts clean
            // instead of being read as the tail of this one.
            cmdOverflow = true;
        }
    }

    // The watchdog. Only armed while something is actually held, so an idle
    // board sends nothing and the bot may stay quiet for as long as it likes.
    if ((anyKeyHeld() || mouseButtons != 0 || cursorButtons != 0) &&
        (millis() - lastCommandMs) > WATCHDOG_MS) {
        releaseEverything();
        lastCommandMs = millis();
        Serial.println(F("EV watchdog"));
    }
}
