# llScriptCrypt — Full Documentation

> Protect your LSL scripts with obfuscation and encryption.
> Part of the TasiaNGC platform for AI Grid.

---

## Function

### llScriptCrypt(action, mode, password, notecard)
Encrypts or decrypts LSL source stored in a notecard.
- `action`: `"enc"` or `"dec"`
- `mode`: see below
- `password`: your secret password
- `notecard`: name of the notecard containing the source

---

## Modes Explained

### `portable`
Minifies and outputs valid LSL source. Works on any OpenSim grid. No encryption — just whitespace removal and packing.
- ✅ Works everywhere
- ❌ No protection — source is readable
- Use for: sharing scripts across grids, clean deployment copy

### `obf` (obfuscated)
Real symbol obfuscation — renames user variables and functions to `_000001`, `_000002`, etc. Removes whitespace and packs into one line.
- ✅ Harder to read, portable (valid LSL)
- ❌ Not encryption — determined attacker can still reverse
- ⚠️ StripComments is OFF by default (safe for all scripts)
- ⚠️ No fake comments added (not real protection, just noise)
- Use for: making scripts harder to read while keeping them portable

### `local-only`
Owner-bound XOR obfuscation. Only the object's owner can decrypt it.
- ✅ Only works for the original object's owner
- ❌ Not portable — only works on your own grid/objects
- Use for: protecting code you never want anyone else to read, even if they copy the object

### `off`
No transformation — returns source as-is (for multi-notecard decryption).

---

## Global Default Mode

Set in `[XEngine]` or `[YEngine]` in `config-include/regions/scripts.ini`:

```ini
[ScriptProtection]
    ScriptProtectionMode = portable
```

Defaults to `portable` if not set.

---

## Quick Reference

| Mode | Portable | Encrypted | Owner-bound |
|------|----------|-----------|-------------|
| `portable` | ✅ | ❌ | ❌ |
| `obf` | ✅ | ❌ | ❌ |
| `local-only` | ❌ | ✅ | ✅ |

---

## Notecard Format

All encrypted output is written as a single long line. For notecard limits (255 chars), split into chunks:

```lsl
integer CHUNK = 230; // safe margin for notecard limit

list toLines(string s)
{
    list out = [];
    integer len = llStringLength(s);
    integer i = 0;
    while (i < len)
    {
        integer j = i + CHUNK - 1;
        out += [llGetSubString(s, i, j)];
        i += CHUNK;
    }
    return out;
}
```

---

## Examples

### 1) Encrypt as portable minified source

```lsl
string PASS = "MySecretPassword123";
string SRC_NOTE = "my_script_source";
string DST_NOTE = "my_script.portable";

default
{
    touch_start(integer n)
    {
        string out = llScriptCrypt("enc", "portable", PASS, SRC_NOTE);
        if (out == "") { llOwnerSay("Encrypt failed"); return; }
        osMakeNotecard(DST_NOTE, toLines(out));
        llOwnerSay("Created: " + DST_NOTE);
    }
}
```

### 2) Encrypt as obfuscated source (harder to read)

```lsl
string PASS = "MySecretPassword123";
string SRC_NOTE = "my_script_source";
string DST_NOTE = "my_script.obf";

default
{
    touch_start(integer n)
    {
        string out = llScriptCrypt("enc", "obf", PASS, SRC_NOTE);
        if (out == "") { llOwnerSay("Encrypt failed"); return; }
        osMakeNotecard(DST_NOTE, toLines(out));
        llOwnerSay("Created: " + DST_NOTE);
    }
}
```

### 3) Encrypt as local-only (owner-bound)

```lsl
string PASS = "MySecretPassword123";
string SRC_NOTE = "my_script_source";
string DST_NOTE = "my_script.local";

default
{
    touch_start(integer n)
    {
        string out = llScriptCrypt("enc", "local-only", PASS, SRC_NOTE);
        if (out == "") { llOwnerSay("Encrypt failed"); return; }
        osMakeNotecard(DST_NOTE, toLines(out));
        llOwnerSay("Created: " + DST_NOTE);
    }
}
```

### 4) Decrypt at runtime

```lsl
string PASS = "MySecretPassword123";
string SRC_NOTE = "my_script.local"; // or .portable / .obf

default
{
    touch_start(integer n)
    {
        string src = llScriptCrypt("dec", "local-only", PASS, SRC_NOTE);
        if (src == "") { llOwnerSay("Decrypt failed"); return; }
        llOwnerSay("Decrypted! Length: " + (string)llStringLength(src));
    }
}
```

---

## WordPress Protector Tool (Offline Obfuscation)

For stronger offline obfuscation with symbol renaming, backup, and restore:

Visit: **WordPress → LSL Protector Workspace** (Premium members)

This tool:
1. Strips comments and renames all user symbols
2. Trims whitespace and packs into one line
3. Stores encrypted original with your password
4. Lets you restore original anytime

See plugin: `tasia-lsl-protector.php` in the codebase.

---

## Troubleshooting

**"Encrypt failed" / empty output:**
- Check notecard name exists and is readable
- Check password is not empty
- Check mode is valid (`portable`, `obf`, `local-only`)

**"Decrypt failed" on local-only:**
- Only the object's owner can decrypt `local-only` payloads
- Password must match the one used during encryption

---

## Security Notes

- `portable` and `obf` are **not encryption** — they are obfuscation. Determined attackers can reverse them.
- `local-only` uses XOR obfuscation bound to the owner's UUID — not military-grade encryption, but stops casual copying.
- For production secrets (API keys, tokens), always use `local-only` and never hardcode secrets in visible notecards.


## 1) Encrypt as portable minified source

```lsl
string PASS = "kurwa";
string SRC_NOTE = "note";
string DST_NOTE = "note.portable";
integer CHUNK = 230;

list toLines(string s)
{
    list out = [];
    integer len = llStringLength(s);
    integer i = 0;
    while (i < len)
    {
        integer j = i + CHUNK - 1;
        out += [llGetSubString(s, i, j)];
        i += CHUNK;
    }
    return out;
}

default
{
    touch_start(integer n)
    {
        string out = llScriptCrypt("enc", "portable", PASS, SRC_NOTE);
        if (out == "") { llOwnerSay("Encrypt portable failed"); return; }
        osMakeNotecard(DST_NOTE, toLines(out));
        llOwnerSay("Created: " + DST_NOTE);
    }
}
```

## 2) Encrypt as obfuscated portable source (example.lsl style)

```lsl
string PASS = "kurwa";
string SRC_NOTE = "note";
string DST_NOTE = "note.obf";
integer CHUNK = 230;

list toLines(string s)
{
    list out = [];
    integer len = llStringLength(s);
    integer i = 0;
    while (i < len)
    {
        integer j = i + CHUNK - 1;
        out += [llGetSubString(s, i, j)];
        i += CHUNK;
    }
    return out;
}

default
{
    touch_start(integer n)
    {
        string out = llScriptCrypt("enc", "obf", PASS, SRC_NOTE);
        if (out == "") { llOwnerSay("Encrypt obf failed"); return; }
        osMakeNotecard(DST_NOTE, toLines(out));
        llOwnerSay("Created: " + DST_NOTE);
    }
}
```

## 3) Encrypt as local-only protected payload

```lsl
string PASS = "kurwa";
string SRC_NOTE = "note";
string DST_NOTE = "note.local";
integer CHUNK = 230;

list toLines(string s)
{
    list out = [];
    integer len = llStringLength(s);
    integer i = 0;
    while (i < len)
    {
        integer j = i + CHUNK - 1;
        out += [llGetSubString(s, i, j)];
        i += CHUNK;
    }
    return out;
}

default
{
    touch_start(integer n)
    {
        string out = llScriptCrypt("enc", "local-only", PASS, SRC_NOTE);
        if (out == "") { llOwnerSay("Encrypt local-only failed"); return; }
        osMakeNotecard(DST_NOTE, toLines(out));
        llOwnerSay("Created: " + DST_NOTE);
    }
}
```

## 4) Decrypt from notecard

```lsl
string PASS = "kurwa";
string SRC_NOTE = "note.local"; // or note.portable / note.obf
string DST_NOTE = "note.dec";
integer CHUNK = 230;

list toLines(string s)
{
    list out = [];
    integer len = llStringLength(s);
    integer i = 0;
    while (i < len)
    {
        integer j = i + CHUNK - 1;
        out += [llGetSubString(s, i, j)];
        i += CHUNK;
    }
    return out;
}

default
{
    touch_start(integer n)
    {
        string out = llScriptCrypt("dec", "local-only", PASS, SRC_NOTE);
        if (out == "") { llOwnerSay("Decrypt failed"); return; }
        osMakeNotecard(DST_NOTE, toLines(out));
        llOwnerSay("Created: " + DST_NOTE);
    }
}
```
