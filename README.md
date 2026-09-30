# My Race My Rules

*by RandomKitten*

Race mods are great, but wouldn't it be great if you weren't restricted by the mod creators 
on how tall your race can be, or what hairstyles are available, or what colors you can 
choose?

My Race My Rules gives you the ability to change all that. Point it at any race added through
[Player Model Lib](https://mods.vintagestory.at/playermodellib) (example: Racial Equality)
and you decide the height range, the classes, the traits, the hairstyles, the colors, and
even whether the race shows up in character creation at all.

## Requirements

- [Player Model Lib](https://mods.vintagestory.at/playermodellib) and its dependencies
- At least one race mod built on it, e.g. [Racial Equality](https://mods.vintagestory.at/racialequality)

## For players

Nothing to do, and nothing to see. Character creation just shows whatever the server allows;
there's no menu and no settings. You may notice a `ModConfig/myracemyrules-servercache-*.json`
appear — that's the mod keeping its own copy of each server's settings. Editing one does
nothing, since the server overwrites it on every connect.

## For server admins

Everything lives in one file on the **server**, created for you on first run:

```
/ModConfig/myracemyrules.json
```

### What you can override, per race

| Setting | What it controls |
|---|---|
| `SizeRange` — `[min, max]` | How short or tall players can make themselves |
| `EyeHeight` | Where the camera sits |
| `MinEyeHeight` / `MaxEyeHeight` | The minimum and maximum camera height |
| `CollisionBox` — `[width, height]` | The player's physical size |
| `MinCollisionBox` / `MaxCollisionBox` — `[width, height]` | The minimum and maximum physical size |
| `Enabled` | Whether the race appears in character creation |
| `Name` | The race's display name in character creation (a literal name, not a translation key). Works for seraph too. |
| `Description` | The race's description in character creation — the blurb shown above the class/trait list (a literal string, not a translation key). Works for seraph too. |
| `AvailableClasses` | Which classes the race can pick (`[]` = all) |
| `ExtraTraits` | Traits granted on top of the class |
| `SkinnableParts` | Hairstyles, facial hair, colors, voice types, and any other appearance option — narrow the choices, hide a section, put back options a race mod removed, or add brand-new ones |

### Example

Orks who can be tiny or towering with all haircolors allowed, no dwarves, and a plainer seraph:

```json
{
  "Overrides": {
    "racialequality:ork": {
      "SizeRange": [0.5, 2.0],
      "Name": "Ork",
      "Description": "Towering brutes, as strong as they are stubborn.",
      "SkinnableParts":{
        "haircolor": { "IncludeDefaultVariants": true }
      }
    },
    "racialequality:dwarf": {
      "Enabled": false
    },
    "seraph": {
      "Description": "The ordinary folk of this world.",
      "SkinnableParts": {
        "hairbase": { "AllowedVariants": ["bald", "short", "medium"] },
        "beard": { "Enabled": false },
        "haircolor": { "RemoveVariants": ["raspberryred", "purple"] }
      }
    }
  }
}
```

- Race keys are `"<mod-id>:<race-code>"`; the default seraph is just `"seraph"`.
- Don't guess at codes. `/myracemyrules` lists every race the mod found, and
  `/myracemyrules <racecode>` lists that race's appearance sections and every variant code in
  them. Both need the `controlserver` privilege.
- **Include a setting to change it, leave it null to keep the race mod's value.** There's no
  separate on/off switch — presence is the switch.

### Putting options back

No need to hunt down missing hairstyle or color names — the mod reads the full list from the
default seraph, which always has everything.

| Setting | Where it goes | What it does |
|---|---|---|
| `IncludeAllDefaultVariants` | race | Every appearance section the race has gets the complete set of options back |
| `IncludeDefaultVariants` | one section | Same, but only for that section — "all hairstyles", "all hair colors" |

Race-wide flags run first, so "give me everything, then take one thing away" works:

```json
"racialequality:ork": {
  "IncludeAllDefaultVariants": true,
  "SkinnableParts": {
    "haircolor": { "RemoveVariants": ["purple"] }
  }
}
```

- The full list comes from the game itself, so new options from game updates are
  included automatically — nothing for you to maintain.
- **A section a race removed entirely is left alone.** Races that can't wear hair delete the
  hairstyle section rather than emptying it, so these settings only restore options inside
  sections the race still has.

### Appearance section options

| Option | What it does |
|---|---|
| `IncludeDefaultVariants` | Add the game's full list of options for this section |
| `AllowedVariants` | Keep only the options you list |
| `RemoveVariants` | Drop the options you list |
| `AddVariants` | Add the options you list |
| `Enabled` | `false` hides the section completely |

### Adding multipart options

To add an option that requires more than just a name  — e.g. **voice type**, which needs its
own sound file — use `AddVariants`.

The simplest form is one line per option. What you put on the right depends on the section:
a **voice** needs a sound file, a **texture** (like a skin or color) needs a texture, a
**shape** needs a shape.

```json
"racialequality:ork": {
  "SkinnableParts": {
    "voicetype": {
      "AddVariants": {
        "ork-deep":  "racialequality:sounds/voice/ork-deep",
        "ork-gruff": "racialequality:sounds/voice/ork-gruff"
      }
    }
  }
}
```

- The **name on the left** is the option's code; the **value on the right** is its file. Sounds
  can be your mod's own (`yourmod:sounds/voice/...`) or one of the game's (`sounds/voice/...`).
- For sections whose options are just a code with no file (like `voicepitch`), you can pass a
  simple list instead: `"voicepitch": { "AddVariants": ["verylow", "low"] }`.
- Need more control? Give an option a full definition instead of a single value:
  `"AddVariants": { "ork-deep": { "sound": "racialequality:sounds/voice/ork-deep" } }`.
- New options get a tidy menu label automatically (the code, capitalized) unless the mod or game
  already provides a translated name — existing names are never overwritten.
- New options are added before `AllowedVariants`/`RemoveVariants`, so those still apply. Re-adding
  the same code updates it rather than creating a duplicate. Added options appear after the next
  world load / reconnect.

Every field a race block accepts:

- `SizeRange` — `[min, max]`  (not configurable for seraph)
- `EyeHeight` — a number (not configurable for seraph)
- `MinEyeHeight` / `MaxEyeHeight` — numbers  (not configurable for seraph)
- `CollisionBox` — `[width, height]` (not configurable for seraph)
- `MinCollisionBox` / `MaxCollisionBox` — `[width, height]`  (not configurable for seraph)
- `Enabled` — `true` / `false`
- `Name` — a display name string (leave out to keep the race mod's name)
- `Description` — a description string shown in character creation (leave out to keep the race mod's description)
- `AvailableClasses` — list of class codes (`[]` means all)
- `ExtraTraits` — list of trait codes
- `IncludeAllDefaultVariants` — `true` / `false`
- `SkinnableParts` — map of section code to the options in the table above

### Applying changes

Edit the file or use the console commands to make changes, then restart the server or reload the world. Players pick the new settings up
when they connect — you don't need to tell them anything.

### Commands

| Command | Privilege | What it does |
|---|---|---|
| `/myracemyrules` | `controlserver` | Lists the races found and which ones you're overriding |
| `/myracemyrules help` | `controlserver` | Shows the command list |
| `/myracemyrules <racecode>` | `controlserver` | Lists that race's appearance sections and their option codes |
| `/myracemyrules <racecode> enable` | `controlserver` | Enables the race in character creation |
| `/myracemyrules <racecode> disable` | `controlserver` | Disables the race in character creation |
| `/myracemyrules <racecode> name <text>` | `controlserver` | Sets the race's display name (use `default` to restore the race mod's name) |
| `/myracemyrules <racecode> description <text>` | `controlserver` | Sets the race's character-creation description (use `default` to restore the race mod's description) |
| `/myracemyrules <racecode> eyeheight <baseValue>` | `controlserver` | Sets the base eye height |
| `/myracemyrules <racecode> collision <width> <height>` | `controlserver` | Sets the base collision box |
| `/myracemyrules <racecode> sizerange <min> <max>` | `controlserver` | Sets the character size range; the minimum cannot be below `0.2` |
| `/myracemyrules <racecode> enableall <part>` | `controlserver` | Restores all default variants for one appearance section |
| `/myracemyrules all sizerange <min> <max>` | `controlserver` | Sets the size range for every detected race |
| `/myracemyrules all enableall <part>` | `controlserver` | Restores all default variants for one section on every detected race |
| `/myracemyrules traits` | `controlserver` | Writes every installed trait (code, name, description, and stat changes) to `Logs/myracemyrules-traits.txt` |

The `/mrmr` command is an alias for `/myracemyrules`, so all of the same forms are available
with the shorter name. Use `default` instead of a value to remove that override and restore the
race's original setting.

## Notes

- Only someone who can edit files on the server can change these settings — players have no
  way to alter or work around them.
- `Name` and `Description` are the race's character-creation display name and blurb. Behind the
  scenes these come from the game's language files (`playermodel-<code>` and `modeldesc-<code>`),
  which is why they work for **seraph** as well as modded races. You give a literal string, not a
  translation key. `default` restores the race mod's original text.
- `EyeHeight` and `CollisionBox` settle in on a player's next connect rather than immediately.
  Neither affects character creation, so it isn't something players run into.
- **The camera ducks under low ceilings automatically.** If a race's eye height is taller than
  the space the player is in — an ork with its eyes at 2.2 blocks in a 2-block tunnel, or a
  kobold with its eyes at 1.1 blocks in a 1-block gap — the camera is lowered to just under the
  ceiling while they are in the low space and returns to normal as soon as they are out. This is
  purely visual and works for every race, tall or short; it never changes the collision box, so
  whether a character *fits* through a space is still decided by the race's `CollisionBox` and
  the game's own sneaking. It is not applied while flying, noclipping, climbing, swimming,
  sitting, mounted, dead, or in spectator mode, where the game positions the camera its own way.
- **Eye color on added facial expressions.** Player Model Lib tints eyes with a color overlay
  that only lands on faces built for it — its own seraph faces, or race faces that expose the
  same `playermodellib-iris` texture. Expressions this mod adds to a race with
  `IncludeDefaultVariants` / `IncludeAllDefaultVariants` are always Player Model Lib's own
  seraph faces, taken from Player Model Lib's loaded seraph model on the client (and from the
  patched player entity on the server) — never from the raw game files, whose vanilla faces
  cannot take eye color. A race whose eye-color section is missing or points elsewhere is
  given one that targets `facialexpression`. If a race's *own* faces don't change color, that
  is something for the race mod to fix in its face shapes. Like other appearance changes, it
  takes effect on the next world load / reconnect.
- `eyeheight` and `collision` console commands set the base values, then multiply those values 
  by the race SizeRange to derive the Min and Max values. These Min and Max values can be 
  manually altered in the JSON config file if needed but will be reset if the console command
  is used again.
