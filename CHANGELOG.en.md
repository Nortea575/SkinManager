# Changelog

## 1.1.4-beta.2 · 2026-11-13

- **Settings layout**: Settings now uses horizontal General, Appearance, and Compatibility tabs, with related options grouped by purpose.
- **UI language selection**: Choose System, Simplified Chinese, or English for the skin library, editor, color picker, preview controls, market interactions, instance window, updates, history, and prompts. Changes apply immediately while preserving editor content. Market account pages retain the website's language; setup and uninstall are available in English and Simplified Chinese.
- **Removed the fixed Codex Simplified Chinese option**: SkinManager no longer changes Codex's language. It restores a language override from an older version only when that override is still managed by SkinManager.

Install over an existing version to keep the skin library, ordering, appearance, and startup settings.

## 1.1.4-beta.1 · 2026-11-12

- **GIF backgrounds**: Importing, saving, exporting, and previewing skins preserves the original animation data. Background images can also be viewed with animation.
- **Free-form color picker**: Replace the fixed palette with a hue ring and saturation/brightness area. Colors support HEX, RGB, and alpha values. Changes preview live, can be committed as a single undo step, or canceled while preserving existing colors and inheritance.
- **More responsive fine-editing previews**: Consecutive adjustments are coalesced into short refresh intervals, and the final value appears immediately when a gesture ends.
- **GIF skins on the market**: Submissions include the minimum manager version, market covers use static images, and downloaded packages retain animation.
- **Startup preferences**: Start the manager when signing in to Windows, and choose whether to start the debug build and apply the last selected skin. Manually opening the manager restores the selection only.
- **Combined compatibility checks**: Magpie launch details and Codex configuration status appear in one area with a shared check entry point.
- **Support author panel**: Adds a GitHub star invitation and repository link.

## 1.1.3 · 2026-11-10

- **Settings in one place**: Manage Simplified Chinese compatibility, Magpie compatibility, and manager appearance in Settings while keeping existing compatibility choices.
- **Connect and apply at startup**: When exactly one eligible debug instance is available, the manager waits for the debugging connection and page to become ready before applying the skin. Choose an instance manually when several are available.
- **Preserve active connections**: Revalidate existing connections by process identity and listening address. A brief debugging endpoint outage no longer clears the connection; missing skin styles can be restored through the current session.
- **Drag to reorder skins**: Drag a skin card to change its position. Clicking to select and scrolling through the list remain available.
- **Consistent interface feedback**: Buttons, menus, panels, lists, and page transitions share consistent animations and follow the system animation setting.
- **Improved Magpie workflow**: Reuse the existing Codex model configuration. When a regular Codex instance is running, confirm switching to debug mode before the manager connects and applies a skin.
- **Unified Chinese and English fonts**: English and numbers use Space Grotesk; Chinese uses the included Noto Sans SC.
- **Market browsing updates**: Sort by recently updated or most liked, filter by tags, switch between grid and list views, and open a dedicated detail page with full descriptions and interaction counts. Covers and downloads remain in the market; preview skins from the local library after installation.
- **Market accounts and interactions**: Sign in to or register a website account in the manager, view and post comments, and like or unlike skins. Upload submissions from the local skin library or a ZIP file and see their review status.
- **Styled popup menus**: The More and Import menus use the manager's visual style and include hover, keyboard focus, disabled, and destructive-action states.
- **Day appearance**: Choose day or night mode in Settings. The choice applies immediately and is remembered separately from each skin's appearance.
- **One window entry for Codex instances**: Use Select window to view, locate, select, or exit Codex instances; the duplicate top-bar entry was removed.
- **Support author entry**: Open the support panel from beside the skin market, read the free-distribution notice, and visit the author's support page.
- **Collapse the preview after closing**: Releasing preview resources also collapses the placeholder area. Reopen it to continue viewing the current skin or editing draft.
- **Offline changelog history**: Switch to History in Version and updates to read release notes included with the app without a network connection.

Upgrade from 1.1.2 or 1.1.3 beta builds to keep the existing skin library, ordering, and compatibility settings. Reapply the skin after upgrading. Stable users do not need to enable the beta channel to check for this release. Market accounts use the website's existing sign-in, registration, and email confirmation flows; submissions and comments follow the site's review rules.

## 1.1.3-beta.1 · 2026-11-10

- **Settings page**: Manage Simplified Chinese and Magpie compatibility while retaining existing choices and connection status.
- **Connect and apply at startup**: When exactly one eligible debug instance is available, wait for the debugging connection before applying the selected skin. Select an instance manually when several are available.
- **Clearer waiting and results**: Show whether the manager is waiting for a debug connection, connected but waiting for the page, or finished applying. Keep the reason for an operation failure visible so it can be addressed before trying again.
- **Drag to reorder skins**: Use the order control in the skin library. The order is retained after restart, and newly imported skins are added to the end.
- **Collapse the preview after closing**: Release preview resources and collapse the placeholder area; reopen it to keep viewing a skin or editing a draft.
- **Offline changelog history**: Switch to History in Version and updates to read bundled release notes without a network connection.

This is a beta release. Install it over an existing version to keep the skin library and compatibility settings, then reapply the skin. Enable Receive beta updates in Version and updates to get later beta releases.

## 1.1.2 · 2026-11-09

- **Optional beta updates**: Enable Receive beta updates in Version and updates to check for both new beta and stable releases. Stable releases are the default. Disabling the beta channel does not downgrade the app; package verification and manual installation confirmation remain unchanged.
- **Refresh connection status after exit**: When Codex exits from the tray, the manager updates connection status and clears invalid targets so the main action can start the debug build again.
- **Recognize existing debug builds**: Verify IPv4 and IPv6 debugging connections against their actual listening addresses to avoid timeouts when another address on the same port is unresponsive.
- **Check instances before applying again**: Revalidate the target process and debugging address after reopening the manager or losing a connection. Exited targets no longer block startup and skin application, and a connection can be re-established after the old background agent exits.
- **Explain unavailable instances**: Distinguish debug launch arguments from an available debugging service to help identify an unready port or a listening-address ownership mismatch.

Install over an existing version to keep the skin library. Existing skin packages need no conversion. Reapply the skin after upgrading.

## 1.1.1 · 2026-11-08

- **Skin creation page**: Start from an image or solid background and adjust light and dark appearances, accent color, panel opacity, corner radius, and background brightness in one place. The name and Create button stay at the bottom; author information is optional.
- **Position the background directly**: Enable Adjust background position and drag the image in the preview. Position is saved with the skin. Image and position adjustments support undo and redo, and thumbnails use the same positioning as the preview.
- **Create a copy from an existing skin**: Choose More → Create based on this skin to reuse its image, author information, and styles. The copy gets a separate ID and leaves the original intact.
- **Dedicated fine editor**: Edit base colors, backgrounds, component properties, interaction states, light and dark variants, and page scenarios. Restore inherited values while keeping component-specific overrides when changing the overall palette.
- **Consistent draft opacity and saved results**: Edits use the skin's own default opacity. Manual apply opacity in the manager is temporarily unavailable while editing and returns after the editor closes.
- **Codex Simplified Chinese option**: Fix Codex's native interface to Simplified Chinese from compatibility settings. Save this choice independently from skins. When disabled, restore the previous language only if it is still managed by the app, preserving a language choice changed by the user later.
- **Compatibility options listed separately**: Show Simplified Chinese and Magpie launch settings with separate descriptions, actions, and statuses for saved-but-pending, active, and failed states.

Install over an existing version to keep the skin library. Existing skin packages need no conversion. Codex language compatibility requires a connectable debug instance, and initial setup may refresh the page. The installer does not include skins; download them from the market or create and import your own.

## 1.1.0 · 2026-11-07

- **Simpler connection flow**: Apply a skin to an existing debug instance or start and apply when no Codex instance is running, using one main action. If a regular instance is still running, the manager asks you to handle it manually and does not close or restart it.
- **Clearer status**: Show connection state separately from skin-application results. When waiting is canceled, keep the last confirmed connection target and indicate that the result needs confirmation.
- **More room in the editor**: Expand the property area to show editing options beside the preview. Opacity applies to both the static preview and the next application; scenario, appearance, and sidebar changes affect the preview only.
- **Magpie checks**: View version, path, and running status, and manually start a verified program. Confirmed version v0.1.3237 does not pass debug arguments when it starts or reopens Codex itself; SkinManager-started Codex can still use Magpie gateway models. The manager does not change Magpie settings or take over its internal launch button. Behavior with other versions has not been verified.

Install over an existing version to keep the skin library. Reapply the skin with the main action after upgrading. The installer does not include skins.

## 1.0.2 · 2026-11-06

- Fixed cards and widgets in Chat replies keeping a black background with light skins.
- Made card backgrounds, body text, secondary text, and borders follow the selected skin while preserving separate link and code-highlight colors.

Install over an existing version to keep the skin library. Reapply the skin after upgrading.

## 1.0.1 · 2026-11-05

- **New interface**: Use a charcoal background and yellow-green accents across the skin library, creator, market, instance manager, and update window.
- **Layout updates**: Give skin images and previews more space, reorganize global tools and connection actions, and make color options scrollable in smaller windows.
- **Interaction feedback**: Refine hover, selection, focus, and window-transition animations while following the system's reduced-motion setting.
- **Updated fonts**: Include open-source Inter and Space Grotesk fonts with SIL Open Font License 1.1 notices.

Install over an existing version to keep the skin library. The installer does not include skins; download them from the market or import them yourself.

## 1.0.0

First release with skin management, creation, preview, and application features.

- **Skin library**: Search, import folders and ZIP files, export, and delete skins.
- **Visual creator**: Edit background images, colors, opacity, and component styles with light and dark variants, page scenarios, undo, and redo.
- **Preview**: View home and conversation scenarios, switch appearance and sidebar, and compare with the native interface. The preview adapts to the window size.
- **Skin market**: Search and filter skins, view author and copyright details, preview large images, and download skins to the library.
- **Apply skins**: Apply or update a skin through a local debugging connection, or restore the native appearance.
- **Instance management**: View running Codex instances, choose a connection target, and exit a selected instance.
- **Updates**: Automatically check for and download new releases; the user starts installation.

Supports Windows x64, the Microsoft Store version of the Codex desktop app, and the WebView2 Runtime. The installer does not include skins; download them from the market or import them yourself.
