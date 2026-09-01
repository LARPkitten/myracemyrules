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
| `CollisionBox` — `[width, height]` | The player's physical size |
| `Enabled` | Whether the race appears in character creation |
| `AvailableClasses` | Which classes the race can pick (`[]` = all) |
| `ExtraTraits` | Traits granted on top of the class |
| `SkinnableParts` | Hairstyles, facial hair, colors, and any other appearance option — narrow the choices, hide a section, put back options a race mod removed, or change any other setting in that section |

### Example

Orcs who can be tiny or towering with every appearance option unlocked, no dwarves, and a
plainer seraph:

```json
{
  "Overrides": {
    "racialequality:ork": {
      "SizeRange": [0.5, 2.0],
      "EnableAllSkinnableParts": true
    },
    "racialequality:dwarf": {
      "Enabled": false
    },
    "seraph": {
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
- **Include a setting to change it, leave it out to keep the race mod's value.** There's no
  separate on/off switch — presence is the switch.

### Putting options back

No need to hunt down missing hairstyle or color names — the mod reads the full list from the
default seraph, which always has everything.

| Setting | Where it goes | What it does |
|---|---|---|
| `IncludeAllDefaultVariants` | race | Every appearance section the race has gets the complete set of options back |
| `IncludeDefaultVariants` | one section | Same, but only for that section — "all hairstyles", "all hair colors" |
| `EnableAllSkinnableParts` | race | Switches every section the race already has back on, without adding anything |
| `EnableAll` | one section | Turns that section on and keeps everything in it |

Race-wide flags run first, so "give me everything, then take one thing away" works:

```json
"racialequality:ork": {
  "IncludeAllDefaultVariants": true,
  "SkinnableParts": {
    "haircolor": { "RemoveVariants": ["purple"] }
  }
}
```

- The full list comes from the game itself, so new hairstyles and colors from game updates are
  included automatically — nothing for you to maintain.
- **A section a race removed entirely is left alone.** Races that can't wear hair delete the
  hairstyle section rather than emptying it, so these settings only restore options inside
  sections the race still has.
- All of it applies on a player's **first connect**, including someone brand new making their
  first character.

### Appearance section options

| Option | What it does |
|---|---|
| `IncludeDefaultVariants` | Add the game's full list of options for this section |
| `AllowedVariants` | Keep only the options you list |
| `RemoveVariants` | Drop the options you list |
| `Enabled` | `false` hides the section completely |
| `EnableAll` | Turn the section on and keep everything in it (ignores the two filters above) |
| `Set` | For the adventurous — a key/value map poked straight into the section's JSON, for settings this mod has no name for, e.g. `"Set": { "useDropDown": true }` |

Every field a race block accepts:

- `SizeRange` — `[min, max]`
- `EyeHeight` — a number
- `CollisionBox` — `[width, height]`
- `Enabled` — `true` / `false`
- `AvailableClasses` — list of class codes (`[]` means all)
- `ExtraTraits` — list of trait codes
- `EnableAllSkinnableParts` — `true` / `false`
- `IncludeAllDefaultVariants` — `true` / `false`
- `SkinnableParts` — map of section code to the options in the table above

### Applying changes

Edit the file, then restart the server or reload the world. Players pick the new settings up
when they connect — you don't need to tell them anything.

### Commands

| Command | Privilege | What it does |
|---|---|---|
| `/myracemyrules` | `controlserver` | Lists the races found and which ones you're overriding |
| `/myracemyrules <racecode>` | `controlserver` | Lists that race's appearance sections and their option codes |

Both are read-only.

## Notes

- Only someone who can edit files on the server can change these settings — players have no
  way to alter or work around them.
- `EyeHeight` and `CollisionBox` settle in on a player's next connect rather than immediately.
  Neither affects character creation, so it isn't something players run into.
