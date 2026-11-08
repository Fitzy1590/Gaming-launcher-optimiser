---
paths: ["**/*.xaml", "**/Views/**/*.cs"]
---

# Windows UI rules (WPF)

The app has its own design system in `src/FitzLauncher.App/Theme.xaml`: a dark, console-style look with an accent colour taken from the selected game's art. Follow Fluent's *contracts* for layout, input, accessibility and window behaviour inside that system. Do not swap it for stock Fluent or WPF-UI styling.

## Tokens only
- **Colours:** `Bg`, `Deep`, `PaneBg`, `Surface`, `Surface2`, `Text`, `Secondary`, `Muted`, `BorderBrush`, `StrongBorder`, `Hover`, plus `Good`/`Warn`/`Bad` for status. Never use hex literals in views. The exception is overlay or preview mock-ups that show what the in-game overlay looks like.
- **Per-game colours:** `Accent`, `AccentSoft`, `Deep`, `Bg`, `PaneBg`, `ArtScrim` and `ArtFade` are retinted per game, so use them with `{DynamicResource}`. Everything else uses `{StaticResource}`.
- **Accent use:** keep the accent for the main action (Play, the selected FPS target) and focus or drag states. It is not decoration.
- **Text styles:** use the existing `H1`, `H2`, `Display`, `MutedText`, `Wrap`, `Figure` (tabular numbers), `Icon` (Segoe Fluent Icons glyphs) and `Keycap`. Fonts come from `UiFont`/`DisplayFont`/`MonoFont`/`IconFont`.
- **Controls:** use `PrimaryButton`, `PlayButton`, `GhostButton`, `ChipToggle`/`ChipButton`, `TargetToggle`, `NavButton` and `Card`. Add a new style to `Theme.xaml` before adding one-off templates in a view.
- **Light/dark:** if you add a light theme, add it as a second token dictionary with the same keys. Views must not branch on theme.

## Layout
- Use a 4 px spacing grid: margins and padding of 4/8/12/16/24/32. Pages get 24-28 px side padding. Cards get 16 px inner padding.
- Use `Grid`/`DockPanel` with star and Auto sizing. No fixed window-sized widths, no absolute positioning except on preview canvases.
- **Responsive:** the minimum window is 980×600. Handheld screens are 1920×1200 at 150-200 % scale; desktops go up to 3440×1440. Long content goes in a `ScrollViewer`, wrapping rows use `WrapPanel`, and every text `TextBlock` uses `TextWrapping="Wrap"` (the `Wrap` style).
- Keep one primary action per area, and put destructive actions behind a confirm.

## Window frame
- Main window: standard frame with the dark title bar (DWM immersive dark mode), or borderless full screen via `MainWindow.SetFullScreen` (Alt+Enter). Never use a custom chrome that breaks snap, Win+arrow, or touch resize.
- Secondary windows: set `Owner`, `WindowStartupLocation="CenterOwner"`, and close on Esc / pad B. Dialogs use `ShowDialog`.
- Don't take the foreground from a running game. Alerts over games use the non-activating `TempAlertWindow` path.

## Input and accessibility
- Every interactive control must be reachable by keyboard and controller: no `Focusable="False"` on things you can click, and `GamepadNavigator` relies on focus order and automation peers.
- Focus must be visible: use the accent border or ring that the styles already provide. Never use `FocusVisualStyle="{x:Null}"` without a replacement trigger.
- **Touch targets:** at least 32 px tall (sliders and scrollbars already have 28-32 px hit areas). Use `Background="Transparent"` on hit areas.
- Body text is at least 12 px, normal text 13-14 px. Never convey state by colour alone; add text or an icon.
- Set `AutomationProperties.Name` on icon-only buttons, and add a `ToolTip` that includes the shortcut.

## Banned
- Generic "AI app" styling: purple or blue gradients, glassmorphism stacks, emoji as icons, drop shadows on everything, centred hero text on functional pages, rounded corners over 8 px on cards, or decorative badges and filler copy.
- Stock WPF chrome showing through, such as the default blue focus rectangles, default scrollbars or default `ComboBox` popups. Restyle via `Theme.xaml`.
- Blocking calls in code-behind; logic belongs in ViewModels.
