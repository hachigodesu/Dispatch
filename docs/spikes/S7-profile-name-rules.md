# S7: Profile name rules

**Date:** 2026-11-12 · **Game:** 1.62.2.1 · **Method:** typing into the game's "create profile" name field
(no profile was created).

| Input | Game's name field |
|---|---|
| `123456789012345678901234567890` | Stops after **20 characters** (`12345678901234567890`) |
| `日本` | Accepted |
| `🚚` (emoji) | **Rejected**: can't be typed |
| `/ \ : * ? " < > \|` | All accepted (the folder name is hex, so Windows file-name rules don't apply) |
| `.` and `-` | Accepted |
| `DispatchTest` (already exists) | Accepted in the field (whether *creating* it would be refused wasn't tested) |
| Leading/trailing space | Not tested |

## Rules for Dispatch's validator
| Rule | Source |
|---|---|
| Not empty or whitespace-only | Dispatch policy |
| No leading/trailing whitespace | Dispatch policy (stricter than unknown game behaviour; avoids look-alike names) |
| At most **20** characters | ✅ S7 |
| No emoji or other characters outside the Basic Multilingual Plane (surrogate pairs) | ✅ S7 (emoji rejected) |
| No control characters | Dispatch policy |
| `/ \ : * ? " < > \|`, `.`, `-` and CJK allowed | ✅ S7. `"` and `\` are escaped in `profile.sii` by `SiiString` |
| Must not match an existing profile name (local or cloud), and its hex folder must not exist | Safety rule 3 (never overwrite), whatever the game allows |
| Same name with different case (e.g. `dispatchtest`) → **warning**, not an error | Different hex folder, but easy to confuse |
| Full path of every copied file ≤ 259 characters | Windows `MAX_PATH`; each CJK character costs 6 hex characters |

## Open (low priority)
- Whether the 20-character limit counts characters or UTF-8 bytes for CJK names. `日本` (2 characters, 6 bytes) was
  accepted; 20 CJK characters weren't tried. The validator counts characters.
