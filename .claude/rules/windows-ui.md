---
paths: ["**/*.xaml", "**/Views/**/*.cs"]
---

# Windows Fluent UI rules (WPF)

The app has its own design system in `src/FitzLauncher.App/Theme.xaml`: a dark, console-style look with an accent colour taken from the selected game's art. Follow Fluent Design's *contracts* (layout, theming, input, accessibility, window behaviour) inside that system. Do not swap it for stock Fluent or third-party WPF styling.

## 1. Layout contracts
- Use a 4 px spacing grid: margins and padding of 4/8/12/16/24/32. Pages get 24-28 px side padding, cards 16 px inner padding.
- Build with `Grid` / `DockPanel` using star and Auto sizing. No fixed window-sized widths and no absolute positioning, except on preview canvases.
- One primary action per area. Destructive actions sit behind a confirm.
- Every text `TextBlock` wraps (`Wrap` style); wrapping rows of chips or tiles use `WrapPanel`.

## 2. Dark/light theme tokens
- **Colours come from tokens only:** `Bg`, `Deep`, `PaneBg`, `Surface`, `Surface2`, `Text`, `Secondary`, `Muted`, `BorderBrush`, `StrongBorder`, `Hover`, and `Good`/`Warn`/`Bad` for status. No hex literals in views (overlay mock-ups that show the in-game overlay are the only exception).
- `Accent`, `AccentSoft`, `Deep`, `Bg`, `PaneBg`, `ArtScrim`, `ArtFade` are retinted per game: reference them with `{DynamicResource}`. Everything else uses `{StaticResource}`.
- Reserve the accent for the main action (Play, the selected FPS target) and for focus and drag states.
- Text styles: `H1`, `H2`, `Display`, `MutedText`, `Wrap`, `Figure` (tabular numbers), `Icon` (Segoe Fluent Icons), `Keycap`. Controls: `PrimaryButton`, `PlayButton`, `GhostButton`, `ChipToggle`/`ChipButton`, `TargetToggle`, `NavButton`, `Card`. Add a style to `Theme.xaml` before writing a one-off template in a view.
- A light theme is a second token dictionary with the same keys as the dark one. Views never branch on the theme, and state is never conveyed by colour alone (add text or an icon).

## 3. Responsive window and grid handling
- Minimum window is 980×600. Targets: handheld 1920×1200 at 150-200 % scale, desktop up to 3440×1440.
- Long pages go in a `ScrollViewer`; game grids and lists virtualise (`VirtualizingPanel.IsVirtualizing`, recycling) and disable horizontal scrolling.
- Columns reflow with `UniformGrid`/`WrapPanel` or star columns instead of fixed pixel widths.
- Main window: standard frame with the dark title bar, or borderless full screen via `MainWindow.SetFullScreen` (Alt+Enter, with minimise/close buttons in the top bar). Never use custom chrome that breaks snap, Win+arrow or touch resize.
- Secondary windows set `Owner`, `WindowStartupLocation="CenterOwner"` and close on Esc / pad B; dialogs use `ShowDialog`.
- Never take the foreground from a running game. Alerts over games use the non-activating `TempAlertWindow` path.

## 4. Keyboard navigation and visible focus
Everything must be usable with keyboard and controller alone; `GamepadNavigator` drives the app through the same focus system.
- **Reachability:** every clickable control is focusable and in a logical Tab order (no `Focusable="False"` or `IsTabStop="False"` on something you can click). Icon-only buttons get `AutomationProperties.Name` and a `ToolTip` that includes the shortcut.
- **Visible focus:** every focusable control shows a high-visibility ring (the accent border or the ring the existing styles provide) with at least 3:1 contrast against its surroundings. Never set `FocusVisualStyle="{x:Null}"` unless the control's template has its own `IsKeyboardFocused` / `IsSelected` trigger that draws the ring. Check this on every new item container.
- **Game grids and lists:** arrow keys move between tiles (`KeyboardNavigation.DirectionalNavigation="Cycle"` or `Continue`), Enter opens or plays, the selected tile keeps the accent border, and the focused tile is scrolled into view (`BringIntoView`).
- **Settings panes:** controls are laid out in reading order so Tab follows the page; labels and values sit on the same row (`DockPanel`), sliders respond to Left/Right and show their value, checkboxes toggle with Space, and every setting works without a mouse.
- **Sizes:** touch and pad targets at least 32 px tall; sliders and scrollbars keep their 28-32 px hit areas (`Background="Transparent"` on hit areas). Body text at least 12 px, normal text 13-14 px.
- Esc / pad B always backs out of the current popup, panel or window.

## Banned
- Generic "AI app" styling: purple/blue gradients, glassmorphism stacks, emoji as icons, drop shadows on everything, centred hero text on functional pages, card corners over 8 px, decorative badges and filler copy.
- Stock WPF chrome showing through (default blue focus rectangles, default scrollbars or `ComboBox` popups): restyle via `Theme.xaml`.
- Blocking calls in code-behind; logic belongs in ViewModels.
