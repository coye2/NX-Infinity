using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;

namespace NxInfinity;

public static class UiText
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Additional = BuildAdditional();

    private static readonly HashSet<string> Authored = new HashSet<string>(StringComparer.Ordinal)
    {
        "", "\n\nCompleted imports were kept. Scan again to continue.", "\n\nDeclared limitations\n", "\n\nRollback is unavailable for this receipt.", "\n\nSaved folders are checked at startup. Manage folders to turn automatic scanning off or scan again.", "\n\nSupported files: NSP, XCI, NRO and NSO. Original files stay where they are. ", "\n\nThe preserved destination snapshot will be restored only if the imported save has not changed. A loaded game or newer progress prevents rollback. The source save stays in place.", "\n\nThis replaces the selected destination save. Its original contents are backed up first. ", "\nBuild  ", "\nCore: ",
        "\nCurrent destination files: ", "\nCurrent: ", "\nDLC containers: ", "\nFrame time: ", "\nNative configuration: ", "\nNext launch preference: ", "\nNext launch: core default", "\nQualification: ", "\nReports stay local until you export and share them. No community upload runs automatically.", "\nRequires: ",
        "\nRevision: ", "\nSource files: ", "\nSource: ", "\nStartup: ", "\nThe report records periodic backend samples, not a full per-present timing trace.", "\nUser: ", " (last issue)", " and press Play again. Your saves, keys and firmware were not changed.", " before recovery?", " game",
        " games", " intervals", " local issue observation(s)", " ms\nFrame count: ", " of ", " proposed changes · this runtime only", " recovered", " related report(s). ", " seconds (hash check through start acknowledgement)", " session did not close cleanly. NX will archive its exact marker and allow another launch. ",
        " · Loader consumption: ", " · Loader observation: ", " · Next launch", " · acknowledged by core", " · active request not read", " · installation could not be verified · ", " · next load required", " · p95 ", " · p99 ", " · restart required",
        " · window hidden for the menu", ", even if another core is selected. ", ", play or resume", ". Choose a folder containing romfs/ or exefs/, or an NX manifest bundle. Imports start disabled until enabled below.", ". The request will be canceled.", "15% · quiet", "35% · default", "A baseline is not a universal bug fix. FPS unlocks, asynchronous draw skipping and experimental profiles are never enabled by this action.", "A changed save, changed native index, active game or pending recovery blocks the import. ", "A file with this name already exists. Existing exports are preserved.",
        "A possible save is only a discovery result. NX Infinity verifies its native identity before copying it into an empty destination.", "A report supplies a reproduction lead; a fix needs its own exact-build and hardware validation.", "Accent", "Action needs attention", "Active value not read", "Active: ", "Actual core observations and local validation reports", "Add DLC", "Add a game folder to start your library.", "Add a local DLC container",
        "Add a local game", "Add a local update container", "Add an individual file", "Add favorite", "Add game folder", "Add game folder · includes subfolders", "Add portable location", "Add profile", "Add update", "Add your games",
        "Added", "All games", "Application receipt", "Applies during the current session", "Apply", "Apply pending setup", "Apply reviewed changes", "Archive marker and recover", "Assign controller", "Audit shader caching, CPU translation caching, native resolution and guest VSync using this core's real setting catalog. Review proposes supported changes; application verifies the effective values and records originals.",
        "Automatic import requires an existing, native-verified empty destination. If none exists, launch the matching game with the correct core and user profile, create a save, close it, then use a reviewed import. Original saves and rollback snapshots are preserved.", "Automatic import: off", "Automatic import: on", "Avatar", "Awaiting verification from this core revision", "A–Z", "Back", "Back up and import snapshot", "Back · keep current selection", "Backspace",
        "Backup action", "Backup, import and restore close the core first. Snapshots must match the exact title, user, backend and format. The previous save is preserved before an import.", "Backups / restore / export", "Backups and manual imports", "Base game", "Base game · no external update", "Baseline receipt", "Baseline requirements", "Baseline review", "Bound to ",
        "Build compatibility and sources", "Buttons and navigation", "Calibrate stick centers", "Cancel", "Cancel launch", "Capture details", "Capture evidence", "Capture guide", "Catalog artwork", "Catalog: henry-debruin/nso-icons · downloaded on request",
        "Change", "Change game", "Change server", "Change this option in core defaults before starting a game.", "Changes apply on the next launch. 60 FPS requires a timing module for the exact game build. Unsupported builds retain Original behavior.", "Changes here override this core's defaults for the selected game.", "Changing cores stops the current session. Saves and caches remain isolated by core.", "Check this core's prerequisites to read its content-selection capabilities.", "Checking game files", "Checking preference…",
        "Choose Review baseline to read the selected core. No settings change during review.", "Choose a Nintendo Switch cover for this library entry.", "Choose a core", "Choose a core to play", "Choose a game", "Choose a game folder. Your files stay where they are.", "Choose a game from your library.", "Choose a library game to inspect its update, DLC and mod selection.", "Choose a new file name", "Choose a preset",
        "Choose a source-built core", "Choose a whole folder once. NX finds supported files in its subfolders. Removing a folder from this list keeps your imported games and original files.", "Choose an extracted NX save snapshot", "Choose an installed core to see its graphics, audio and system settings.", "Choose an item.", "Choose core", "Choose next-session mode", "Choose prod.keys, title.keys, dev.keys or console.keys", "Choose resolution", "Choose the destination save",
        "Choose the emulator data folder", "Choose the emulator's data folder, not an individual save folder. Kenji/Ryujinx sources are verified through their native save index before importing.", "Choose the gameplay controller", "Choose the launcher language", "Choose update and DLC", "Choose your default launcher. Switching views during a session keeps this preference.", "Choose your local files once. NX Infinity provisions each supported installed core and keeps the sources for cores you add later.", "Choose your local firmware source", "Clear search", "Close",
        "Close NX Infinity", "Close the game and back up?", "Close the game to verify backups?", "Collection", "Community server", "Compare the same scene, game build, hardware, renderer mode and timing point. Use separate cold-cache and warm-cache runs. These measurements do not establish gameplay speed or rendering correctness.", "Compatibility", "Compiler object cleanup", "Compiler storage reuse", "Configure",
        "Confirm", "Connect to the selected core by reading its settings or prerequisites first.", "Content", "Content action needs attention", "Content evidence", "Continue", "Controller", "Controller command transport", "Controller for player 1", "Controller request",
        "Controller selection needs attention", "Controls", "Copy invitation", "Core capabilities", "Core defaults", "Core defaults · change scope", "Core for ", "Core optimizations", "Core unconfirmed", "Core: ",
        "Cores", "Could not save language", "Cover & game details", "Cover & metadata", "Create Desktop Shortcut", "Create backup", "Current selection  ·  Nintendo Europe", "Custom resolution scale", "Custom scale within the core's range", "DLC selection",
        "Default: ", "Delete", "Desktop", "Details", "Details & reset", "Details only", "Diagnostics", "Disabled  ·  ", "Discover native saves", "Done",
        "Each core uses its own save and cache directories.", "Edit cover & details", "Edit search: ", "Edit text", "Effective: ", "Effective: not yet observed", "Emulation FPS: ", "Enabled  ·  ", "Enter  Open     Esc  Back     Tab  Move focus", "Enter a number",
        "Enter a scale", "Error", "Evidence and validation", "Exact hardware binding unavailable", "Existing files are preserved in Content. Choose an installed core to identify games; updates and DLC stay separate.", "Experimental timing patches are selected by game version and executable ID. Some are downloaded from their author on first use and cached. Choose Original to remove the enhancement on the next launch. A patch cannot guarantee stable 60 FPS on every device.", "Export Diagnostics", "Export a verified snapshot ZIP", "Export diagnostics", "Export includes their title/build, core, graphics identity and native capture boundary. Add the matching scene recording when you choose to share a report.",
        "Export local report", "Export measurements", "Export performance and diagnostics", "Export save snapshot", "Extracted firmware folder", "Favorite", "Favorited", "Favorites", "File unavailable", "Find cover",
        "Find cover & details", "Finish or cancel the current operation before reviewing recovery.", "Finish saving in the game first. The core will stop before NX Infinity reads its save directory.", "Firmware results distinguish a readable version from a complete package checked against the selected source. Retry failed cores can recover a matching interrupted firmware transaction. Game requirements and retail behavior still need validation.", "Folders", "Format: ", "Frame pacing", "Frame pacing, shader progress, and supported core controls", "Frame-rate compatibility", "Fullscreen uses your display's desktop mode. Internal game resolution stays at your selected scale. F11 toggles the game window; Guide / HOME opens HOME. If the center button is intercepted, hold View / Select + Menu / Start for half a second. Closing the game window returns to your library.",
        "GAME DETAILS", "Game compatibility", "Game core", "Game details", "Game display", "Game folder", "Game folder scan", "Game folders", "Game identity", "Game improvements",
        "Game interaction unavailable", "Game keyboard", "Game message", "Game request: ", "Game: ", "Global preference; individual games can override it.", "Go offline", "Graphics", "Graphics & display", "Graphics & game display",
        "Graphics, stability, and controller reports with recorded evidence", "HOME", "HOME audio & startup", "HOME backdrop", "HOME controls the launcher. Game fullscreen and resolution are available in Graphics & display.", "HOME fallback: View / Select + Menu / Start · ", "HOME options", "HOME sound volume", "Host private session", "IMPROVEMENTS",
        "IN YOUR LIBRARY", "Identified updates and DLC appear in Content. Unverified entries still need native inspection before use.", "Identify", "Identity and evidence", "Identity details", "Import Kenji / Ryujinx save", "Import a Kenji / Ryujinx save", "Import a preset you already have. Nothing turns on automatically.", "Import and advanced options", "Import image",
        "Import matching saves", "Import mod", "Import preset", "Import snapshot", "Import snapshot folder", "Import this exact save snapshot?", "Imported from: ", "Imported in this attempt: ", "Imported saves", "Imported saves and guarded rollback",
        "Improvements", "Included", "Indexed save imported", "Inspect again", "Inspect exact source findings", "Inspect selected core", "Inspect selected game", "Inspect the game after setting up local prerequisites to unlock title-specific actions.", "Inspect this game with the selected core first. Mod imports require its verified full executable build ID.", "Install a complete NX core package to enable HOME audio.",
        "Install firmware for all cores", "Install keys for all cores", "Install local firmware", "Interface sounds: ", "It does not delete, import or replace saves, keys, firmware, settings or caches. Active profile locks and interrupted data transactions remain protected.", "Join code", "Keep each core's own caches. Cache loading covers previously encountered work; new game scenes may still need compilation. NX does not download shader packs or delete warm caches automatically.", "Keyboard / mouse · Enter: done   Esc: cancel   A: select   B: back   +: done", "Keys & firmware · all cores", "LB / RB change views. Use the mouse wheel or left / right to browse covers.",
        "LOCAL GAME", "Language", "Language changes apply immediately. Game titles, filenames and player text stay unchanged.", "Language for NX Infinity. Games keep their own language settings.", "Last issue", "Last issue · ", "Last local export: ", "Latest attempt", "Latest recorded setup per source and core. A result may belong to an earlier installation; Inspect selected core checks the current one. Full history remains in Setup results & limitations.", "Latest scan and per-core results",
        "Launch fullscreen", "Launch prepared game", "Launch windowed", "Launcher language", "Library", "Library view options", "Live telemetry", "Local content", "Local game  ·  ", "Local graphics observations",
        "Local library details", "Local prerequisites, updates, DLC, and exact-build mods", "Locations and results", "Locations checked", "Manage portable locations", "Manage saved", "Map buttons & calibrate", "Measured FPS describes frame output. Normal game speed still needs a wall-time comparison of gameplay, physics, animation and audio.", "Measured game FPS: unavailable", "Mod inventory unavailable",
        "Mod receipt", "Mods", "Mods: game build not identified", "More", "Mute", "NINTENDO SWITCH", "NOW PLAYING", "NX Infinity", "NX Infinity checks common emulator folders and copies verified matches into empty save destinations. Your original saves stay in place.", "Name · A–Z",
        "Native content evidence", "Native destinations checked: ", "Native identity and evidence", "Native mappings, verified backups, and recoverable imports", "Native shader progress unavailable", "Native state: ", "Native timing capture", "Native update and DLC selection is unavailable for this core revision.", "Navigation, selection, back and notification sounds. Game audio is controlled separately.", "New capture and play",
        "Next", "Next launch: ", "Nextendo Online", "Nintendo Switch  ·  ", "No NX mod bundles are installed for this build.", "No available choices.", "No connected core", "No controller", "No core selected", "No files need review.",
        "No gamepad detected. Connect one to show its available selection.", "No games in this view", "No games match this view.", "No games yet", "No indexed save destinations", "No matching observation is in this catalogue. Choose all reported cores or all games to broaden the review. This does not establish that the current game is unaffected.", "No matching settings. Try another category or search.", "No matching themes. Clear the search or Favorites filter.", "No presets available yet", "No recovery needed for ",
        "No running core", "No scan completed yet.", "No verified native save mapping has been read. Run the game and save, then discover its native saves.", "Not identified", "OK", "Observation details", "Observed graphics identity", "Off", "On", "On for the running game",
        "Only your search terms are sent. Selected artwork stays available offline. Game files and native identity stay local.", "Open Logs", "Open Orbit", "Open game", "Open mod directory", "Open settings", "Open-source licenses", "Operation in progress", "Optional graphics and performance presets for your games.", "Options",
        "Orbit keeps its saves separate from offline play. Each player needs the same core, game update and DLC. Choose Local Wireless inside the game.", "Original", "Original core policy.", "Original-byte evidence", "Overview", "Pause before restoring live settings. A before-load change ends when this runtime closes; your saved core preferences are retained for the next launch.", "Performance", "Pinned core capabilities", "Pinned source observations", "Play",
        "Play fullscreen", "Play game", "Play windowed", "Portable save locations", "Possible saves", "Preset color", "Preview startup sound", "Previewing your current appearance", "Previous", "Quick Play",
        "Quick Play selects an exact-build discovery pool. Create or join a room using the game's Local Wireless menu. The relay connection alone does not confirm gameplay.", "Read active graphics device", "Read active values", "Read connection state", "Read exact-build mods", "Read native mount state", "Read native selection", "Read the native selection before changing it. Content is validated by the core; a folder name does not establish an installed version.", "Read the native selection first.", "Read window state",
        "Reading mod inventory…", "Ready", "Receipt", "Receipt: ", "Recent", "Recent captures", "Recently added", "Recently played", "Record current issue", "Recover ",
        "Recovery not applied to ", "Recovery unavailable", "Refresh", "Refresh local covers", "Release all buttons, then press the button or trigger to bind.", "Release both sticks, then calibrate their center.", "Release gamepad · use native keyboard where supported", "Reload catalog", "Reload required", "Remove",
        "Remove avatar", "Remove entry", "Remove from library", "Remove from list", "Remove this library entry?", "Rename", "Renderer mode for the next launch", "Renderer threading", "Reorder controllers", "Repair Installation",
        "Replace this save with the reviewed snapshot?", "Replay NX Infinity intro", "Reproduce a rendering issue", "Requested: ", "Requires a new core session", "Reset catalog details", "Resolution scale", "Resolve the last issue", "Restore backup", "Restore core preferences for this game",
        "Restore defaults", "Restore original", "Restore previous ", "Restore previous mod selection", "Restore recorded originals", "Restore the pre-import save?", "Restore this backup?", "Restore this snapshot and preserve the current save", "Resume", "Resume game",
        "Retry failed cores", "Return to all games or change your search.", "Return to desktop mode", "Return to library", "Review baseline", "Review details", "Review files", "Review selected core recovery", "Review settings before playing", "Review the local details.",
        "Review the selected core to check whether it exposes renderer threading.", "Review the selected core to read its compiled performance policies. Older builds may not report these details.", "Reviewed ", "Revision: ", "Roll back this save import?", "Roll back unchanged import", "Rollback import", "Rollback is available only while the destination still matches the imported snapshot. New game progress is preserved.", "Rollback last import", "Run Health Check",
        "RyuLDN relay · guest Internet access off · no automatic router changes. Server reachability is checked when preparing a session.", "SESSION PAUSED", "STARTING GAME", "Save as preset", "Save import receipt", "Save import, migration and restore tools are available in offline mode. Stop the game and choose Go offline in Orbit to use them. Online gameplay cannot overwrite the offline profile.", "Save profile", "Save scan and import results", "Saves", "Saves from other emulators",
        "Scan all saved folders", "Scan for saves", "Scan this folder now", "Search", "Search Nintendo Switch games", "Search Nintendo’s public catalog, then choose the matching edition.", "Search settings", "Search your games", "Search your library", "Select a Switch control, then press the physical button. Guide remains reserved for HOME.",
        "Select a game to get started", "Select a game to review its build-specific patch availability. Native-60 games keep their original synchronization.", "Select a location to remove it from future scans. The emulator folder and its saves remain in place.", "Select an installed core before reviewing recovery.", "Selected-device identity recorded", "Session and application options", "Set color", "Set up local content", "Setting: ", "Settings",
        "Settings for this game", "Setup results & limitations", "Shader cache consumer: ", "Shader cache queue", "Shader link result", "Shaders and caches", "Shared setup results", "Shift", "Show all games", "Show invitation",
        "Show more games (", "Showing all games", "Showing all reported cores", "Showing selected core", "Showing selected game", "Single GPU thread", "Skip intro", "Snapshot cleanup needs attention", "Snapshot verification needs exclusive access to this core's save storage.", "Sort your library",
        "Source inspections", "Sources and reuse observations", "Space", "Square cover", "Start NX Infinity in", "Start the game once with this core to create its native save, then close it. NX will not invent a save-index entry.", "Starting game", "Startup animation: ", "State: ", "Stick dead zone",
        "Stop ", "Stop and back up", "Stop and configure", "Stop and verify", "Stop game", "Stop game and review", "Stop this game before removing its library entry.", "Stop tracking this folder", "Surprise me", "Switch game",
        "Switch games?", "Symbols", "Takes effect on the next game load", "The active game's profile remains in use. Its setup is deferred until the next fresh launch; other available cores can finish now.", "The core validates the actual content identity before selecting this update.", "The current game will close. Save your progress in the game before switching.", "The current game will stop before its content selection changes. Save in the game before continuing.", "The current save will be preserved before replacement. Use the same title, user and core when you relaunch.", "The external source is never modified. This is a one-way reviewed import, not a live two-way merge.", "The first scan starts when the launcher opens.",
        "The game did not accept this response: ", "The game requested confirmation.", "The game requested native controller setup. This development frontend supports one assigned player; this applet's requested player/style setup has not been qualified and will be canceled. Return to Controls to review the actual assignment.", "The game will close. The imported snapshot must match this native save identity. A verified copy of the current save will be preserved before replacement.", "The game will close. The save preserved before the last import becomes active, and the current version is backed up.", "The launcher could not save this preference. Your previous language is still selected.", "The pinned cores differ in their asynchronous shader and resource-flush paths. Source presence is recorded separately from a reproduced game fix.", "The previous ", "The previous marker was archived. Select ", "The reviewed snapshot was imported and the previous save was preserved. The receipt appears under Imported saves. Test the game before removing any backups.",
        "The source stays untouched. NX reads its native index on a private copy; no title or user IDs are guessed. Close the destination game's session first.", "Themes", "There are no ready-to-use presets for this game and core. You can adjust graphics and sound in Settings.", "These are policies included in this exact core build. The cache readouts show actual use, and captures show observed frame pacing. A 60 FPS game option still requires the correct game build, normal gameplay speed and sufficient hardware performance.", "This action applies only to ", "This copies matching saves into NX Infinity. Playing elsewhere does not overwrite your newer NX progress, and changes are not written back to the other emulator.", "This core changed. Restore its defaults, then choose your settings again.", "This core does not expose a selectable renderer construction mode. Its native behavior remains in use.", "This core has no abandoned session marker. Select it and try launching again. No saves, keys or firmware were changed.", "This core requested an interaction that this frontend does not support: ",
        "This core still has an active or paused game. Save in-game first. Continuing stops that session normally before checking its marker; unsaved progress cannot be restored.", "This game field accepts ASCII characters.", "This game field does not accept that character.", "This game needs a native inspection. Set up local content, then inspect it to unlock title-specific core and content options.", "This game · change scope", "This policy version has not been reviewed by this launcher.", "This removes the library record. The game file, saves and mod payloads stay in their current locations.", "This saves a preference for this exact core revision. Finish saving in the game before relaunching. The active renderer will not change.", "This view needs attention", "Threaded rendering",
        "Threaded rendering can reduce stalls while new effects compile. First-use hitches can still occur. Try the single GPU thread option if a game has compatibility problems. Changes apply on the next launch.", "Title ", "Title ID  ", "Title not verified", "Title: ", "Toggle a container, then choose Use this DLC selection. Entitlement and native content checks still apply.", "Trigger threshold", "Truncated-cache recovery", "Try a different title or return to all games.", "Turn off",
        "Turn on", "Type with your keyboard or click the keys below", "USB wireless receivers work through the same controller list. For a GameSir Tarantula Pro on PC, use its paired receiver in XInput mode; it may appear with an Xbox controller name. After reconnecting or changing modes, confirm its new connection here.", "Unavailable", "Unavailable in this build. NX Infinity still needs a registered Nextendo application and its native account/game-service adapter. Orbit Local Wireless does not provide Nextendo Online.", "Unverified", "Update for ", "Update: ", "Updates and DLC", "Updates, DLC and files awaiting native identification stay outside the game library.",
        "Use Add portable location to select an external Ryujinx, Canary or Kenji data folder when it is not in the usual Ryujinx location. The game and user must match exactly.", "Use a finite number within the core's advertised range.", "Use a number within the range shown by the core, with a period for decimals.", "Use avatar", "Use global preference", "Use original default", "Use the default core", "Use the game's numeric keys.", "Use this DLC selection", "User ",
        "Validation could not finish", "Validation still required", "Verified backups", "Version ", "View game", "Waiting for the game…", "Which emulator uses this folder?", "Window and event scheduling", "Your favorite games, all in one place.", "Your games",
        "Your library", "Your source is retained once. Each installed core reports whether it supports and validates that source.", "ZIP package or XCI update source", "firmware source", "not established", "not measured", "not observed", "not reported", "not stored", "observed",
        "reported", "stored", "unavailable", "unknown", "unqualified", "—", "✓ Selected: native keyboard / no frontend gamepad"
    };

    private static readonly IReadOnlyDictionary<string, string> Italian = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Automatic import requires an existing, native-verified empty destination. If none exists, launch the matching game with the correct core and user profile, create a save, close it, then use a reviewed import. Original saves and rollback snapshots are preserved."] = "L'importazione automatica richiede una destinazione vuota già esistente e verificata dal core. Se non esiste, avvia il gioco corrispondente con il core e il profilo utente corretti, crea un salvataggio, chiudi il gioco e usa l'importazione con verifica. I salvataggi originali e le copie di ripristino vengono conservati.",
        ["NX Infinity Core keeps its own caches. Cache loading covers previously encountered work; new game scenes may still need compilation. NX does not download shader packs or delete warm caches automatically."] = "NX Infinity Core conserva le proprie cache. Il caricamento della cache comprende il lavoro già incontrato; nuove scene di gioco possono ancora richiedere la compilazione. NX non scarica pacchetti di shader e non elimina automaticamente le cache già compilate.",
        ["\n\nCompleted imports were kept. Scan again to continue."] = "\n\nLe importazioni completate sono state conservate. Ripeti la scansione per continuare.",
        ["\n\nDeclared limitations\n"] = "\n\nLimitazioni dichiarate\n",
        ["\n\nRollback is unavailable for this receipt."] = "\n\nIl ripristino non è disponibile per questa operazione.",
        ["\n\nSaved folders are checked at startup. Manage folders to turn automatic scanning off or scan again."] = "\n\nLe cartelle salvate vengono controllate all'avvio. In Gestisci cartelle puoi disattivare la scansione automatica o ripeterla.",
        ["\n\nSupported files: NSP, XCI, NRO and NSO. Original files stay where they are. "] = "\n\nFile supportati: NSP, XCI, NRO e NSO. I file originali restano al loro posto. ",
        ["\n\nThe preserved destination snapshot will be restored only if the imported save has not changed. A loaded game or newer progress prevents rollback. The source save stays in place."] = "\n\nLa copia originale della destinazione verrà ripristinata solo se il salvataggio importato non è cambiato. Un gioco caricato o progressi più recenti impediscono il ripristino. Il salvataggio di origine resta al suo posto.",
        ["\n\nThis replaces the selected destination save. Its original contents are backed up first. "] = "\n\nQuesta operazione sostituisce il salvataggio di destinazione selezionato. Prima viene creato un backup dei contenuti originali. ",
        ["\nBuild  "] = "\nBuild  ",
        ["\nChoose a different core"] = "\nScegli un altro core",
        ["\nCore: "] = "\nCore: ",
        ["\nCurrent destination files: "] = "\nFile attuali della destinazione: ",
        ["\nCurrent: "] = "\nAttuale: ",
        ["\nDLC containers: "] = "\nContenitori DLC: ",
        ["\nFrame time: "] = "\nTempo per fotogramma: ",
        ["\nNative configuration: "] = "\nConfigurazione nativa: ",
        ["\nNext launch preference: "] = "\nPreferenza per il prossimo avvio: ",
        ["\nNext launch: core default"] = "\nProssimo avvio: impostazione predefinita del core",
        ["\nQualification: "] = "\nValidazione: ",
        ["\nReports stay local until you export and share them. No community upload runs automatically."] = "\nI report restano locali finché non li esporti e condividi. Non vengono inviati automaticamente alla community.",
        ["\nRequires: "] = "\nRichiede: ",
        ["\nRevision "] = "\nRevisione ",
        ["\nRevision: "] = "\nRevisione: ",
        ["\nSource files: "] = "\nFile di origine: ",
        ["\nSource: "] = "\nOrigine: ",
        ["\nStartup: "] = "\nAvvio: ",
        ["\nThe report records periodic backend samples, not a full per-present timing trace."] = "\nIl report registra campioni periodici del core, non una traccia completa dei tempi di ogni fotogramma presentato.",
        ["\nUser: "] = "\nUtente: ",
        [" (last issue)"] = " (ultimo problema)",
        [" and press Play again. Your saves, keys and firmware were not changed."] = " e premi di nuovo Gioca. Salvataggi, chiavi e firmware non sono stati modificati.",
        [" before recovery?"] = " prima del ripristino?",
        [" game"] = " gioco",
        [" games"] = " giochi",
        [" intervals"] = " intervalli",
        [" local issue observation(s)"] = " segnalazioni locali",
        [" ms\nFrame count: "] = " ms\nNumero di fotogrammi: ",
        [" of "] = " di ",
        [" proposed changes · this runtime only"] = " modifiche proposte · solo questa sessione",
        [" recovered"] = " ripristinato",
        [" related report(s). "] = " report correlati. ",
        [" seconds (hash check through start acknowledgement)"] = " secondi (dalla verifica dell'hash alla conferma di avvio)",
        [" session did not close cleanly. NX will archive its exact marker and allow another launch. "] = " non si è chiusa correttamente. NX archivierà il marcatore originale e consentirà un nuovo avvio. ",
        [" · Higher rendering resolution"] = " · risoluzione di rendering superiore",
        [" · Loader consumption: "] = " · Utilizzo da parte del caricatore: ",
        [" · Loader observation: "] = " · Osservazione del caricatore: ",
        [" · Lower rendering resolution"] = " · risoluzione di rendering inferiore",
        [" · Next launch"] = " · Prossimo avvio",
        [" · Original scale"] = " · scala originale",
        [" · acknowledged by core"] = " · confermato dal core",
        [" · active request not read"] = " · richiesta attiva non letta",
        [" · installation could not be verified · "] = " · impossibile verificare l'installazione · ",
        [" · next load required"] = " · richiesto un nuovo caricamento",
        [" · p95 "] = " · p95 ",
        [" · p99 "] = " · p99 ",
        [" · restart required"] = " · riavvio necessario",
        [" · window hidden for the menu"] = " · finestra nascosta per il menu",
        [" — choose a folder"] = " — scegli una cartella",
        [", even if another core is selected. "] = ", anche se è selezionato un altro core. ",
        [", play or resume"] = ", gioca o riprendi",
        [". Imported bundles start disabled; enabled mounts are checked again before native loading."] = ". I pacchetti importati sono inizialmente disattivati; quelli attivati vengono ricontrollati prima del caricamento nativo.",
        [". The request will be canceled."] = ". La richiesta verrà annullata.",
        ["15% · quiet"] = "15% · basso",
        ["35% · default"] = "35% · predefinito",
        ["A baseline is not a universal bug fix. FPS unlocks, asynchronous draw skipping and experimental profiles are never enabled by this action."] = "La configurazione di base non risolve ogni problema. Questa azione non attiva lo sblocco degli FPS, il salto asincrono dei disegni o i profili sperimentali.",
        ["A changed save, changed native index, active game or pending recovery blocks the import. "] = "Un salvataggio o un indice nativo modificato, un gioco attivo o un ripristino in sospeso impediscono l'importazione. ",
        ["A file with this name already exists. Existing exports are preserved."] = "Esiste già un file con questo nome. Le esportazioni esistenti vengono conservate.",
        ["A home for your next adventure."] = "Il punto di partenza per la tua prossima avventura.",
        ["A possible save is only a discovery result. NX Infinity verifies its native identity before copying it into an empty destination."] = "Un possibile salvataggio è soltanto un risultato della ricerca. NX Infinity ne verifica l'identità nativa prima di copiarlo in una destinazione vuota.",
        ["A report supplies a reproduction lead; a fix needs its own exact-build and hardware validation."] = "Un report aiuta a riprodurre il problema; una correzione richiede una verifica sulla stessa build e sullo stesso hardware.",
        ["Action needs attention"] = "L'azione richiede attenzione",
        ["Active value not read"] = "Valore attivo non letto",
        ["Active: "] = "Attivo: ",
        ["Actual core observations and local validation reports"] = "Dati effettivi del core e report di verifica locali",
        ["Add DLC"] = "Aggiungi DLC",
        ["Add a local DLC container"] = "Aggiungi un contenitore DLC locale",
        ["Add a local game"] = "Aggiungi un gioco locale",
        ["Add a local game to make yourself at home."] = "Aggiungi un gioco locale per iniziare.",
        ["Add a local game to start your collection. Choose its cover, set up your controls, and keep everything in one place."] = "Aggiungi un gioco locale per iniziare la tua raccolta. Scegli la copertina, configura i comandi e tieni tutto in un unico posto.",
        ["Add a local update container"] = "Aggiungi un contenitore di aggiornamento locale",
        ["Add an individual file"] = "Aggiungi un singolo file",
        ["Add favorite"] = "Aggiungi ai preferiti",
        ["Add game folder"] = "Aggiungi cartella dei giochi",
        ["Add game folder · includes subfolders"] = "Aggiungi cartella dei giochi · include le sottocartelle",
        ["Add portable location"] = "Aggiungi percorso portatile",
        ["Add update"] = "Aggiungi aggiornamento",
        ["Added"] = "Aggiunto",
        ["All"] = "Tutte",
        ["All games"] = "Tutti i giochi",
        ["Application receipt"] = "Riepilogo dell'applicazione",
        ["Applies during the current session"] = "Si applica durante la sessione attuale",
        ["Apply pending setup"] = "Applica la configurazione in sospeso",
        ["Apply reviewed changes"] = "Applica le modifiche esaminate",
        ["Archive marker and recover"] = "Archivia il marcatore e ripristina",
        ["Assign controller"] = "Assegna controller",
        ["Audio"] = "Audio",
        ["Audit shader caching, CPU translation caching, native resolution and guest VSync using this core's real setting catalog. Review proposes supported changes; application verifies the effective values and records originals."] = "Controlla la cache degli shader, la cache di traduzione della CPU, la risoluzione nativa e il VSync del gioco usando le impostazioni effettive di questo core. La verifica propone modifiche supportate; l'applicazione controlla i valori effettivi e conserva quelli originali.",
        ["Automatic import works with Eden, Citron, Kenji and Ryujinx. Native indexes verify Kenji/Ryujinx matches; only destinations without game progress are eligible. Existing progress requires the reviewed import button. Original saves and rollback snapshots are preserved. Close the game first."] = "L'importazione automatica funziona con Eden, Citron, Kenji e Ryujinx. Gli indici nativi verificano le corrispondenze Kenji/Ryujinx; sono idonee solo le destinazioni senza progressi di gioco. Per progressi già presenti usa l'importazione con verifica. Salvataggi originali e copie di ripristino vengono conservati. Chiudi prima il gioco.",
        ["Automatic import: off"] = "Importazione automatica: disattivata",
        ["Automatic import: on"] = "Importazione automatica: attivata",
        ["Awaiting verification from this core revision"] = "In attesa di verifica per questa revisione del core",
        ["A–Z"] = "A–Z",
        ["Back"] = "Indietro",
        ["Back in your library"] = "Ritorno alla raccolta",
        ["Back up and import snapshot"] = "Crea backup e importa copia",
        ["Back · keep current selection"] = "Indietro · mantieni la selezione attuale",
        ["Backspace"] = "Cancella",
        ["Backup action"] = "Azione di backup",
        ["Backup, import and restore close the core first. Snapshots must match the exact title, user, backend and format. The previous save is preserved before an import."] = "Backup, importazione e ripristino chiudono prima il core. Le copie devono corrispondere esattamente a gioco, utente, core e formato. Il salvataggio precedente viene conservato prima dell'importazione.",
        ["Backups / restore / export"] = "Backup / ripristino / esportazione",
        ["Backups and manual imports"] = "Backup e importazioni manuali",
        ["Base game"] = "Gioco base",
        ["Base game · no external update"] = "Gioco base · nessun aggiornamento esterno",
        ["Baseline receipt"] = "Riepilogo della configurazione di base",
        ["Baseline requirements"] = "Requisiti della configurazione di base",
        ["Baseline review"] = "Verifica della configurazione di base",
        ["Bound to "] = "Associato a ",
        ["Buttons and navigation"] = "Pulsanti e navigazione",
        ["COLLECTION"] = "RACCOLTA",
        ["CORE"] = "CORE",
        ["Cancel"] = "Annulla",
        ["Cancel launch"] = "Annulla avvio",
        ["Canceled"] = "Annullato",
        ["Capture details"] = "Dettagli della registrazione",
        ["Capture evidence"] = "Registra dati di verifica",
        ["Capture guide"] = "Guida alla registrazione",
        ["Catalog artwork"] = "Immagine dal catalogo",
        ["Change"] = "Modifica",
        ["Change core: "] = "Cambia core: ",
        ["Change game"] = "Cambia gioco",
        ["Change this option in core defaults before starting a game."] = "Modifica questa opzione nelle impostazioni predefinite del core prima di avviare un gioco.",
        ["Changes here override this core's defaults for the selected game."] = "Queste modifiche sostituiscono le impostazioni predefinite del core per il gioco selezionato.",
        ["Changing cores stops the current session. Saves and caches remain isolated by core."] = "Cambiare core interrompe la sessione attuale. Salvataggi e cache rimangono separati per ciascun core.",
        ["Check this core's prerequisites to read its content-selection capabilities."] = "Controlla i prerequisiti di questo core per leggerne le opzioni di selezione dei contenuti.",
        ["Checking game files"] = "Verifica dei file del gioco",
        ["Checking local content prerequisites"] = "Verifica dei prerequisiti dei contenuti locali",
        ["Checking preference…"] = "Verifica della preferenza…",
        ["Choose Review baseline to read the selected core. No settings change during review."] = "Scegli Verifica configurazione di base per leggere il core selezionato. Durante la verifica non viene modificata alcuna impostazione.",
        ["Choose a Nintendo Switch cover for this library entry."] = "Scegli una copertina Nintendo Switch per questo gioco della raccolta.",
        ["Choose a core"] = "Scegli un core",
        ["Choose a core to play"] = "Scegli un core per giocare",
        ["Choose a core; no core installed"] = "Scegli un core; nessun core installato",
        ["Choose a folder"] = "Scegli una cartella",
        ["Choose a game"] = "Scegli un gioco",
        ["Choose a game from your library."] = "Scegli un gioco dalla raccolta.",
        ["Choose a library game to inspect its update, DLC and mod selection."] = "Scegli un gioco della raccolta per esaminarne aggiornamenti, DLC e mod.",
        ["Choose a new file name"] = "Scegli un nuovo nome del file",
        ["Choose a preset"] = "Scegli un profilo",
        ["Choose a source-built core"] = "Scegli un core compilato dal sorgente",
        ["Choose a whole folder once. NX finds supported files in its subfolders. Removing a folder from this list keeps your imported games and original files."] = "Scegli una cartella: NX cercherà i file supportati anche nelle sottocartelle. Rimuoverla da questo elenco non elimina i giochi importati né i file originali.",
        ["Choose an NX mod bundle folder"] = "Scegli una cartella di un pacchetto mod NX",
        ["Choose an extracted NX save snapshot"] = "Scegli una copia di salvataggio NX estratta",
        ["Choose an installed core to see its graphics, audio and system settings."] = "Scegli un core installato per visualizzarne le impostazioni grafiche, audio e di sistema.",
        ["Choose an item."] = "Scegli un elemento.",
        ["Choose core"] = "Scegli core",
        ["Choose next-session mode"] = "Scegli la modalità per la prossima sessione",
        ["Choose prod.keys, title.keys, dev.keys or console.keys"] = "Scegli prod.keys, title.keys, dev.keys o console.keys",
        ["Choose resolution"] = "Scegli risoluzione",
        ["Choose the destination save"] = "Scegli il salvataggio di destinazione",
        ["Choose the emulator data folder"] = "Scegli la cartella dati dell'emulatore",
        ["Choose the emulator's data folder, not an individual save folder. Kenji/Ryujinx sources are verified through their native save index before importing."] = "Scegli la cartella dati dell'emulatore, non una singola cartella di salvataggio. Le origini Kenji/Ryujinx vengono verificate tramite l'indice nativo dei salvataggi prima dell'importazione.",
        ["Choose the gameplay controller"] = "Scegli il controller di gioco",
        ["Choose the launcher language"] = "Scegli la lingua del launcher",
        ["Choose update and DLC"] = "Scegli aggiornamento e DLC",
        ["Choose your local files once. NX Infinity provisions each supported installed core and keeps the sources for cores you add later."] = "Scegli i file locali una sola volta. NX Infinity li configura per ogni core installato compatibile e li conserva per i core aggiunti in seguito.",
        ["Choose your local firmware source"] = "Scegli l'origine del firmware locale",
        ["Clear search"] = "Cancella ricerca",
        ["Close"] = "Chiudi",
        ["Close NX Infinity"] = "Chiudi NX Infinity",
        ["Close the game and back up?"] = "Chiudere il gioco e creare un backup?",
        ["Close the game to verify backups?"] = "Chiudere il gioco per verificare i backup?",
        ["Closing game and releasing its resources"] = "Chiusura del gioco e rilascio delle risorse",
        ["Closing · "] = "Chiusura · ",
        ["Collection"] = "Raccolta",
        ["Compare the same scene, game build, hardware, renderer mode and timing point. Use separate cold-cache and warm-cache runs. These measurements do not establish gameplay speed or rendering correctness."] = "Confronta la stessa scena, build del gioco, configurazione hardware, modalità di rendering e punto di misura. Separa le prove con cache vuota e cache già popolata. Queste misure non dimostrano la velocità corretta del gioco né la correttezza grafica.",
        ["Compatibility"] = "Compatibilità",
        ["Compiler object cleanup"] = "Pulizia degli oggetti del compilatore",
        ["Compiler storage reuse"] = "Riutilizzo della memoria del compilatore",
        ["Confirm"] = "Conferma",
        ["Connect to the selected core by reading its settings or prerequisites first."] = "Collegati al core selezionato leggendo prima le impostazioni o i prerequisiti.",
        ["Content"] = "Contenuti",
        ["Content added"] = "Contenuto aggiunto",
        ["Content evidence"] = "Dati sui contenuti",
        ["Continue"] = "Continua",
        ["Controller"] = "Controller",
        ["Controller command transport"] = "Trasporto dei comandi del controller",
        ["Controller for player 1"] = "Controller del giocatore 1",
        ["Controller request"] = "Richiesta del controller",
        ["Controller selection needs attention"] = "La selezione del controller richiede attenzione",
        ["Controls"] = "Comandi",
        ["Core capabilities"] = "Funzionalità del core",
        ["Core defaults"] = "Predefiniti del core",
        ["Core defaults · change scope"] = "Predefiniti del core · cambia ambito",
        ["Core for "] = "Core per ",
        ["Core optimizations"] = "Ottimizzazioni del core",
        ["Core unconfirmed"] = "Core non confermato",
        ["Cores"] = "Core",
        ["Could not save language"] = "Impossibile salvare la lingua",
        ["Cover & game details"] = "Copertina e dettagli del gioco",
        ["Cover & metadata"] = "Copertina e metadati",
        ["Create backup"] = "Crea backup",
        ["Current selection  ·  Nintendo Europe"] = "Selezione attuale  ·  Nintendo Europa",
        ["Custom resolution scale"] = "Scala di risoluzione personalizzata",
        ["Custom scale within the core's range"] = "Scala personalizzata entro i limiti del core",
        ["D-pad / stick  Move     South  Confirm     East  Back     LB / RB  Sections     Guide  Game menu"] = "Croce / stick  Muovi     Sud  Conferma     Est  Indietro     LB / RB  Sezioni     Guide  Menu di gioco",
        ["DLC selection"] = "Selezione DLC",
        ["Default: "] = "Predefinito: ",
        ["Delete"] = "Elimina",
        ["Desktop"] = "Desktop",
        ["Details"] = "Dettagli",
        ["Details & reset"] = "Dettagli e ripristino",
        ["Details only"] = "Solo dettagli",
        ["Diagnostic report exported"] = "Report diagnostico esportato",
        ["Diagnostics"] = "Diagnostica",
        ["Disabled  ·  "] = "Disattivato  ·  ",
        ["Discover native saves"] = "Rileva salvataggi nativi",
        ["Documents"] = "Documenti",
        ["Done"] = "Fine",
        ["Downloads"] = "Download",
        ["Each core uses its own save and cache directories."] = "Ogni core usa le proprie cartelle di salvataggio e cache.",
        ["Edit cover & details"] = "Modifica copertina e dettagli",
        ["Edit search: "] = "Modifica ricerca: ",
        ["Edit text"] = "Modifica testo",
        ["Effective: "] = "Effettivo: ",
        ["Effective: not yet observed"] = "Effettivo: non ancora osservato",
        ["Emulation FPS: "] = "FPS di emulazione: ",
        ["Enabled  ·  "] = "Attivato  ·  ",
        ["Enhancements"] = "Miglioramenti",
        ["Enter  Open     Esc  Back     Tab  Move focus"] = "Invio  Apri     Esc  Indietro     Tab  Sposta selezione",
        ["Enter a number"] = "Inserisci un numero",
        ["Enter a scale"] = "Inserisci una scala",
        ["Error"] = "Errore",
        ["Evidence and validation"] = "Dati e verifiche",
        ["Exact hardware binding unavailable"] = "Associazione hardware esatta non disponibile",
        ["Export a verified snapshot ZIP"] = "Esporta una copia verificata in formato ZIP",
        ["Export diagnostics"] = "Esporta diagnostica",
        ["Export includes their title/build, core, graphics identity and native capture boundary. Add the matching scene recording when you choose to share a report."] = "L'esportazione include gioco e build, core, identità grafica e limiti della registrazione nativa. Aggiungi una registrazione della stessa scena quando condividi il report.",
        ["Export local diagnostic ZIP"] = "Esporta ZIP diagnostico locale",
        ["Export local report"] = "Esporta report locale",
        ["Export measurements"] = "Esporta misurazioni",
        ["Export performance and diagnostics"] = "Esporta prestazioni e diagnostica",
        ["Export save snapshot"] = "Esporta copia del salvataggio",
        ["Extracted firmware folder"] = "Cartella del firmware estratto",
        ["FPS unavailable"] = "FPS non disponibili",
        ["Favorite"] = "Preferito",
        ["Favorited"] = "Nei preferiti",
        ["Favorites"] = "Preferiti",
        ["File       "] = "File       ",
        ["File name"] = "Nome del file",
        ["Filter this folder"] = "Filtra questa cartella",
        ["Find cover"] = "Trova copertina",
        ["Find cover & details"] = "Trova copertina e dettagli",
        ["Finish or cancel the current operation before reviewing recovery."] = "Completa o annulla l'operazione in corso prima di esaminare il ripristino.",
        ["Finish saving in the game first. The core will stop before NX Infinity reads its save directory."] = "Salva prima i progressi nel gioco. Il core verrà chiuso prima che NX Infinity legga la cartella di salvataggio.",
        ["Firmware results distinguish a readable version from a complete package checked against the selected source. Retry failed cores can recover a matching interrupted firmware transaction. Game requirements and retail behavior still need validation."] = "I risultati distinguono una versione leggibile da un pacchetto completo verificato rispetto all'origine selezionata. Riprova core non riusciti può recuperare una transazione firmware interrotta compatibile. I requisiti e il comportamento dei giochi richiedono ancora una verifica.",
        ["Folder   "] = "Cartella   ",
        ["Folders"] = "Cartelle",
        ["Frame pacing"] = "Regolarità dei fotogrammi",
        ["Frame pacing, shader progress, and supported core controls"] = "Regolarità dei fotogrammi, stato degli shader e comandi supportati dal core",
        ["Fullscreen"] = "Schermo intero",
        ["Fullscreen uses your display's desktop mode. Internal game resolution stays at your selected scale. F11 toggles the game window; Guide / HOME opens HOME. If the center button is intercepted, hold View / Select + Menu / Start for half a second. Closing the game window returns to your library."] = "Lo schermo intero usa la modalità desktop del monitor. La risoluzione interna del gioco mantiene la scala selezionata. F11 cambia la modalità della finestra di gioco; Guide / HOME apre HOME. Se il pulsante centrale è intercettato, tieni premuti View / Select + Menu / Start per mezzo secondo. Chiudere la finestra di gioco riporta alla raccolta.",
        ["GAME DETAILS"] = "DETTAGLI DEL GIOCO",
        ["Game compatibility"] = "Compatibilità del gioco",
        ["Game core"] = "Core del gioco",
        ["Game details"] = "Dettagli del gioco",
        ["Game display"] = "Schermo di gioco",
        ["Game folder"] = "Cartella dei giochi",
        ["Game folder scan"] = "Scansione della cartella dei giochi",
        ["Game folders"] = "Cartelle dei giochi",
        ["Game identity"] = "Identità del gioco",
        ["Game improvements"] = "Miglioramenti del gioco",
        ["Game interaction unavailable"] = "Interazione di gioco non disponibile",
        ["Game is already closed"] = "Il gioco è già chiuso",
        ["Game keyboard"] = "Tastiera di gioco",
        ["Game message"] = "Messaggio del gioco",
        ["Game request: "] = "Richiesta del gioco: ",
        ["Game: "] = "Gioco: ",
        ["Go"] = "Vai",
        ["Graphics"] = "Grafica",
        ["Graphics & display"] = "Grafica e schermo",
        ["Graphics & game display"] = "Grafica e schermo di gioco",
        ["Graphics, stability, and controller reports with recorded evidence"] = "Report su grafica, stabilità e controller con dati registrati",
        ["HOME"] = "HOME",
        ["HOME audio & startup"] = "Audio e avvio HOME",
        ["HOME controls the launcher. Game fullscreen and resolution are available in Graphics & display."] = "HOME controlla il launcher. Schermo intero e risoluzione del gioco sono disponibili in Grafica e schermo.",
        ["HOME fallback: View / Select + Menu / Start · "] = "Scorciatoia HOME: View / Select + Menu / Start · ",
        ["HOME options"] = "Opzioni HOME",
        ["HOME sound volume"] = "Volume audio HOME",
        ["IMPROVEMENTS"] = "MIGLIORAMENTI",
        ["IN YOUR LIBRARY"] = "NELLA TUA RACCOLTA",
        ["Identified updates and DLC appear in Content. Unverified entries still need native inspection before use."] = "Gli aggiornamenti e i DLC identificati compaiono in Contenuti. Gli elementi non verificati richiedono ancora un'ispezione nativa prima dell'uso.",
        ["Identity and evidence"] = "Identità e dati",
        ["Identity details"] = "Dettagli dell'identità",
        ["Import Kenji / Ryujinx save"] = "Importa salvataggio Kenji / Ryujinx",
        ["Import a Kenji / Ryujinx save"] = "Importa un salvataggio Kenji / Ryujinx",
        ["Import a preset you already have. Nothing turns on automatically."] = "Importa un profilo che possiedi già. Nulla viene attivato automaticamente.",
        ["Import and advanced options"] = "Importazione e opzioni avanzate",
        ["Import matching saves"] = "Importa salvataggi corrispondenti",
        ["Import mod bundle"] = "Importa pacchetto mod",
        ["Import preset"] = "Importa profilo",
        ["Import snapshot"] = "Importa copia",
        ["Import snapshot folder"] = "Importa cartella della copia",
        ["Import this exact save snapshot?"] = "Importare questa copia esatta del salvataggio?",
        ["Imported saves"] = "Salvataggi importati",
        ["Improvements"] = "Miglioramenti",
        ["Included"] = "Incluso",
        ["Indexed save imported"] = "Salvataggio indicizzato importato",
        ["Initialized"] = "Inizializzato",
        ["Inspect again"] = "Esamina di nuovo",
        ["Inspect exact source findings"] = "Esamina i risultati del sorgente esatto",
        ["Inspect selected core"] = "Esamina core selezionato",
        ["Inspect selected game"] = "Esamina gioco selezionato",
        ["Inspect the game after setting up local prerequisites to unlock title-specific actions."] = "Esamina il gioco dopo aver configurato i prerequisiti locali per sbloccare le azioni specifiche.",
        ["Install a complete NX core package to enable HOME audio."] = "Installa un pacchetto core NX completo per abilitare l'audio HOME.",
        ["Install firmware for all cores"] = "Installa firmware per tutti i core",
        ["Install keys for all cores"] = "Installa chiavi per tutti i core",
        ["Install local firmware"] = "Installa firmware locale",
        ["Interface sounds: "] = "Suoni dell’interfaccia: ",
        ["Invalid file name"] = "Nome del file non valido",
        ["It does not delete, import or replace saves, keys, firmware, settings or caches. Active profile locks and interrupted data transactions remain protected."] = "Non elimina, importa o sostituisce salvataggi, chiavi, firmware, impostazioni o cache. I blocchi dei profili attivi e le transazioni interrotte restano protetti.",
        ["Keep each core's own caches. Cache loading covers previously encountered work; new game scenes may still need compilation. NX does not download shader packs or delete warm caches automatically."] = "Mantieni le cache di ciascun core. Le cache contengono il lavoro già incontrato; le nuove scene possono richiedere compilazione. NX non scarica pacchetti shader e non elimina automaticamente le cache esistenti.",
        ["Keyboard"] = "Tastiera",
        ["Keyboard / mouse · Enter: done   Esc: cancel   A: select   B: back   +: done"] = "Tastiera / mouse · Invio: fine   Esc: annulla   A: seleziona   B: indietro   +: fine",
        ["Keys & firmware · all cores"] = "Chiavi e firmware · tutti i core",
        ["LB / RB change views. Use the mouse wheel or left / right to browse covers."] = "LB / RB cambiano vista. Usa la rotella del mouse o sinistra / destra per sfogliare le copertine.",
        ["LOCAL GAME"] = "GIOCO LOCALE",
        ["Language"] = "Lingua",
        ["Language changes apply immediately. Game titles, filenames and player text stay unchanged."] = "Le modifiche si applicano subito. Titoli dei giochi, nomi dei file e testo inserito dai giocatori restano invariati.",
        ["Language for NX Infinity. Games keep their own language settings."] = "Lingua di NX Infinity. I giochi mantengono le proprie impostazioni della lingua.",
        ["Last issue"] = "Ultimo problema",
        ["Last issue · "] = "Ultimo problema · ",
        ["Last local export: "] = "Ultima esportazione locale: ",
        ["Latest recorded setup per source and core. A result may belong to an earlier installation; Inspect selected core checks the current one. Full history remains in Setup results & limitations."] = "Ultima configurazione registrata per origine e core. Un risultato può riferirsi a un'installazione precedente; Esamina core selezionato verifica quella attuale. La cronologia completa è in Risultati e limitazioni della configurazione.",
        ["Launch fullscreen"] = "Avvia a schermo intero",
        ["Launch windowed"] = "Avvia in finestra",
        ["Launcher language"] = "Lingua del launcher",
        ["Library"] = "Raccolta",
        ["Library  /  Game details"] = "Raccolta  /  Dettagli del gioco",
        ["Library view options"] = "Opzioni della raccolta",
        ["Live telemetry"] = "Telemetria in tempo reale",
        ["Loaded"] = "Caricato",
        ["Local content"] = "Contenuti locali",
        ["Local folder path"] = "Percorso della cartella locale",
        ["Local game"] = "Gioco locale",
        ["Local game  ·  "] = "Gioco locale  ·  ",
        ["Local graphics observations"] = "Osservazioni grafiche locali",
        ["Local library details"] = "Dettagli locali della raccolta",
        ["Local prerequisites, updates, DLC, and exact-build mods"] = "Prerequisiti locali, aggiornamenti, DLC e mod per la build esatta",
        ["Locations"] = "Percorsi",
        ["Locations and results"] = "Percorsi e risultati",
        ["Locations on this computer"] = "Percorsi su questo computer",
        ["MANAGE"] = "GESTIONE",
        ["Mod receipt"] = "Riepilogo mod",
        ["Mods"] = "Mod",
        ["Mute"] = "Disattiva audio",
        ["NINTENDO SWITCH"] = "NINTENDO SWITCH",
        ["NOW PLAYING"] = "IN ESECUZIONE",
        ["NX INFINITY"] = "NX INFINITY",
        ["NX Infinity"] = "NX Infinity",
        ["NX Infinity checks common emulator folders and copies verified matches into empty save destinations. Your original saves stay in place."] = "NX Infinity controlla le cartelle comuni degli emulatori e copia le corrispondenze verificate nelle destinazioni di salvataggio vuote. I salvataggi originali restano al loro posto.",
        ["Name · A–Z"] = "Nome · A–Z",
        ["Native content evidence"] = "Dati dei contenuti nativi",
        ["Native destinations checked: "] = "Destinazioni native controllate: ",
        ["Native destinations verified"] = "Destinazioni native verificate",
        ["Native identity and evidence"] = "Identità e dati nativi",
        ["Native mappings, verified backups, and recoverable imports"] = "Corrispondenze native, backup verificati e importazioni recuperabili",
        ["Native shader cache load returned · starting game"] = "Cache shader nativa caricata · avvio del gioco",
        ["Native shader progress unavailable"] = "Stato degli shader nativi non disponibile",
        ["Native state: "] = "Stato nativo: ",
        ["Native timing capture"] = "Registrazione dei tempi nativi",
        ["Native update and DLC selection is unavailable for this core revision."] = "La selezione nativa di aggiornamenti e DLC non è disponibile per questa revisione del core.",
        ["Navigation, selection, back and notification sounds. Game audio is controlled separately."] = "Suoni di navigazione, selezione, ritorno e notifica. L'audio del gioco si regola separatamente.",
        ["New capture and play"] = "Nuova registrazione e avvio",
        ["Next launch: "] = "Prossimo avvio: ",
        ["Nintendo Switch  ·  "] = "Nintendo Switch  ·  ",
        ["No NX mod bundles are installed for this build."] = "Nessun pacchetto mod NX installato per questa build.",
        ["No available choices."] = "Nessuna scelta disponibile.",
        ["No connected core"] = "Nessun core collegato",
        ["No controller"] = "Nessun controller",
        ["No core installed. Open the core selector for available choices."] = "Nessun core installato. Apri la selezione core per visualizzare le opzioni disponibili.",
        ["No core selected"] = "Nessun core selezionato",
        ["No gamepad detected. Connect one to show its available selection."] = "Nessun controller rilevato. Collegane uno per poterlo selezionare.",
        ["No games found"] = "Nessun gioco trovato",
        ["No games in this view"] = "Nessun gioco in questa vista",
        ["No games match this view."] = "Nessun gioco corrisponde a questa vista.",
        ["No indexed save destinations"] = "Nessuna destinazione di salvataggio indicizzata",
        ["No matching entries in this folder."] = "Nessun elemento corrispondente in questa cartella.",
        ["No matching observation is in this catalogue. Choose all reported cores or all games to broaden the review. This does not establish that the current game is unaffected."] = "Nessuna osservazione corrispondente nel catalogo. Scegli tutti i core segnalati o tutti i giochi per ampliare la ricerca. Questo non dimostra che il gioco attuale sia privo del problema.",
        ["No matching settings. Try another category or search."] = "Nessuna impostazione corrispondente. Prova un'altra categoria o ricerca.",
        ["No presets available yet"] = "Nessun profilo ancora disponibile",
        ["No recovery needed for "] = "Nessun ripristino necessario per ",
        ["No running core"] = "Nessun core in esecuzione",
        ["No verified native save mapping has been read. Run the game and save, then discover its native saves."] = "Nessuna corrispondenza nativa verificata è stata letta. Avvia il gioco, salva e poi rileva i salvataggi nativi.",
        ["OK"] = "OK",
        ["Observation details"] = "Dettagli dell'osservazione",
        ["Observed graphics identity"] = "Identità grafica osservata",
        ["Off"] = "No",
        ["On"] = "Sì",
        ["On for the running game"] = "Attivo per il gioco in esecuzione",
        ["Only your search terms are sent. Selected artwork stays available offline. Game files and native identity stay local."] = "Vengono inviati solo i termini di ricerca. Le immagini selezionate restano disponibili offline. File di gioco e identità nativa restano locali.",
        ["Open game"] = "Apri gioco",
        ["Open settings"] = "Apri impostazioni",
        ["Opening game menu"] = "Apertura del menu di gioco",
        ["Operation in progress"] = "Operazione in corso",
        ["Optional graphics and performance presets for your games."] = "Profili grafici e prestazionali facoltativi per i tuoi giochi.",
        ["Options"] = "Opzioni",
        ["Original"] = "Originale",
        ["Original core policy."] = "Criterio originale del core.",
        ["Packaging native shader cache"] = "Creazione del pacchetto della cache shader nativa",
        ["Pause before restoring live settings. A before-load change ends when this runtime closes; your saved core preferences are retained for the next launch."] = "Metti in pausa prima di ripristinare le impostazioni attive. Una modifica prima del caricamento termina alla chiusura della sessione; le preferenze salvate del core restano valide per il prossimo avvio.",
        ["Paused"] = "In pausa",
        ["Paused · "] = "In pausa · ",
        ["Paused · ready to resume"] = "In pausa · pronto a riprendere",
        ["Performance"] = "Prestazioni",
        ["Pinned core capabilities"] = "Funzionalità della versione fissata del core",
        ["Pinned source observations"] = "Osservazioni sul sorgente della versione fissata",
        ["Play"] = "Gioca",
        ["Play fullscreen"] = "Gioca a schermo intero",
        ["Play game"] = "Gioca",
        ["Play windowed"] = "Gioca in finestra",
        ["Playing"] = "In esecuzione",
        ["Playing · "] = "In esecuzione · ",
        ["Portable save locations"] = "Percorsi dei salvataggi portatili",
        ["Preparing native shader cache"] = "Preparazione della cache shader nativa",
        ["Preparing the first game frame"] = "Preparazione del primo fotogramma del gioco",
        ["Preview startup sound"] = "Ascolta suono di avvio",
        ["Qualification still required"] = "Validazione ancora necessaria",
        ["Read active graphics device"] = "Leggi dispositivo grafico attivo",
        ["Read active values"] = "Leggi valori attivi",
        ["Read exact-build mods"] = "Leggi mod per la build esatta",
        ["Read native mount state"] = "Leggi stato dei contenuti montati",
        ["Read native selection"] = "Leggi selezione nativa",
        ["Read the native selection before changing it. Content is validated by the core; a folder name does not establish an installed version."] = "Leggi la selezione nativa prima di modificarla. Il core verifica i contenuti; il nome di una cartella non dimostra quale versione sia installata.",
        ["Read the native selection first."] = "Leggi prima la selezione nativa.",
        ["Read window state"] = "Leggi stato della finestra",
        ["Reading this core's settings…"] = "Lettura delle impostazioni del core…",
        ["Ready"] = "Pronto",
        ["Receipt"] = "Riepilogo",
        ["Recent"] = "Recenti",
        ["Recent captures"] = "Registrazioni recenti",
        ["Recently added"] = "Aggiunti di recente",
        ["Recently played"] = "Giocati di recente",
        ["Record current issue"] = "Registra il problema attuale",
        ["Recover "] = "Ripristina ",
        ["Recovery not applied to "] = "Ripristino non applicato a ",
        ["Recovery unavailable"] = "Ripristino non disponibile",
        ["Refresh"] = "Aggiorna",
        ["Release gamepad · use native keyboard where supported"] = "Rilascia controller · usa la tastiera nativa se supportata",
        ["Reload required"] = "Ricaricamento necessario",
        ["Remove entry"] = "Rimuovi voce",
        ["Remove from library"] = "Rimuovi dalla raccolta",
        ["Remove this library entry?"] = "Rimuovere questa voce dalla raccolta?",
        ["Renderer mode for the next launch"] = "Modalità di rendering per il prossimo avvio",
        ["Renderer threading"] = "Thread di rendering",
        ["Replace this save with the reviewed snapshot?"] = "Sostituire questo salvataggio con la copia verificata?",
        ["Replay NX Infinity intro"] = "Rivedi introduzione NX Infinity",
        ["Reproduce a rendering issue"] = "Riproduci un problema grafico",
        ["Requested: "] = "Richiesto: ",
        ["Requires a new core session"] = "Richiede una nuova sessione del core",
        ["Reset catalog details"] = "Ripristina dettagli del catalogo",
        ["Resolution scale"] = "Scala di risoluzione",
        ["Resolve the last issue"] = "Risolvi l'ultimo problema",
        ["Restore backup"] = "Ripristina backup",
        ["Restore core preferences for this game"] = "Ripristina preferenze del core per questo gioco",
        ["Restore previous "] = "Ripristina precedente ",
        ["Restore previous mod selection"] = "Ripristina selezione mod precedente",
        ["Restore recorded originals"] = "Ripristina i valori originali registrati",
        ["Restore the pre-import save?"] = "Ripristinare il salvataggio precedente all'importazione?",
        ["Restore this backup?"] = "Ripristinare questo backup?",
        ["Restore this snapshot and preserve the current save"] = "Ripristina questa copia e conserva il salvataggio attuale",
        ["Resume"] = "Riprendi",
        ["Resume game"] = "Riprendi gioco",
        ["Retry failed cores"] = "Riprova core non riusciti",
        ["Return to all games or change your search."] = "Torna a tutti i giochi o modifica la ricerca.",
        ["Return to desktop mode"] = "Torna alla modalità desktop",
        ["Return to library"] = "Torna alla raccolta",
        ["Review baseline"] = "Verifica configurazione di base",
        ["Review details"] = "Esamina dettagli",
        ["Review selected core recovery"] = "Esamina ripristino del core selezionato",
        ["Review settings before playing"] = "Controlla le impostazioni prima di giocare",
        ["Review the local details."] = "Esamina i dettagli locali.",
        ["Review the selected core to check whether it exposes renderer threading."] = "Esamina il core selezionato per verificare se espone le opzioni dei thread di rendering.",
        ["Review the selected core to read its compiled performance policies. Older builds may not report these details."] = "Esamina il core selezionato per leggerne i criteri prestazionali compilati. Le build precedenti potrebbero non riportarli.",
        ["Reviewed "] = "Esaminato ",
        ["Roll back this save import?"] = "Annullare questa importazione del salvataggio?",
        ["Roll back unchanged import"] = "Annulla importazione non modificata",
        ["Rollback import"] = "Annulla importazione",
        ["Rollback is available only while the destination still matches the imported snapshot. New game progress is preserved."] = "Il ripristino è disponibile solo finché la destinazione corrisponde ancora alla copia importata. I nuovi progressi di gioco vengono preservati.",
        ["Rollback last import"] = "Annulla ultima importazione",
        ["Running"] = "In esecuzione",
        ["SESSION PAUSED"] = "SESSIONE IN PAUSA",
        ["STARTING GAME"] = "AVVIO DEL GIOCO",
        ["Save import receipt"] = "Riepilogo dell'importazione del salvataggio",
        ["Save scan and import results"] = "Risultati della ricerca e importazione dei salvataggi",
        ["Saves"] = "Salvataggi",
        ["Saves from other emulators"] = "Salvataggi da altri emulatori",
        ["Scan all saved folders"] = "Scansiona tutte le cartelle salvate",
        ["Scan for saves"] = "Cerca salvataggi",
        ["Scan this folder now"] = "Scansiona questa cartella ora",
        ["Search"] = "Cerca",
        ["Search Nintendo Switch games"] = "Cerca giochi Nintendo Switch",
        ["Search Nintendo’s public catalog, then choose the matching edition."] = "Cerca nel catalogo pubblico Nintendo e scegli l'edizione corrispondente.",
        ["Search settings"] = "Cerca impostazioni",
        ["Search your games"] = "Cerca nei tuoi giochi",
        ["Search your library"] = "Cerca nella raccolta",
        ["Select a game to get started"] = "Seleziona un gioco per iniziare",
        ["Select a location to remove it from future scans. The emulator folder and its saves remain in place."] = "Seleziona un percorso per escluderlo dalle prossime scansioni. La cartella dell'emulatore e i suoi salvataggi restano al loro posto.",
        ["Select an installed core before reviewing recovery."] = "Seleziona un core installato prima di esaminare il ripristino.",
        ["Selected-device identity recorded"] = "Identità del dispositivo selezionato registrata",
        ["Session"] = "Sessione",
        ["Session and application options"] = "Opzioni della sessione e dell'applicazione",
        ["Session ready"] = "Sessione pronta",
        ["Set up local content"] = "Configura contenuti locali",
        ["Setting: "] = "Impostazione: ",
        ["Settings"] = "Impostazioni",
        ["Settings for this game"] = "Impostazioni per questo gioco",
        ["Setup results & limitations"] = "Risultati e limitazioni della configurazione",
        ["Shader cache consumer: "] = "Utilizzo della cache shader: ",
        ["Shader cache queue"] = "Coda della cache shader",
        ["Shader link result"] = "Risultato del collegamento shader",
        ["Shaders and caches"] = "Shader e cache",
        ["Shared setup results"] = "Risultati della configurazione condivisa",
        ["Shift"] = "Maiusc",
        ["Show all games"] = "Mostra tutti i giochi",
        ["Show more games ("] = "Mostra altri giochi (",
        ["Showing all games"] = "Tutti i giochi",
        ["Showing all reported cores"] = "Tutti i core segnalati",
        ["Showing selected core"] = "Core selezionato",
        ["Showing selected game"] = "Gioco selezionato",
        ["Showing the first 500 entries. Filter the folder to narrow the list."] = "Sono mostrati i primi 500 elementi. Filtra la cartella per restringere l’elenco.",
        ["Single GPU thread"] = "Un solo thread GPU",
        ["Skip intro"] = "Salta introduzione",
        ["Snapshot cleanup needs attention"] = "La pulizia della copia richiede attenzione",
        ["Snapshot verification needs exclusive access to this core's save storage."] = "La verifica delle copie richiede accesso esclusivo ai salvataggi di questo core.",
        ["Sort your library"] = "Ordina la raccolta",
        ["Sound volume: {0:P0}"] = "Volume audio: {0:P0}",
        ["Source inspections"] = "Ispezioni del sorgente",
        ["Space"] = "Spazio",
        ["Square cover"] = "Copertina quadrata",
        ["Start next time in HOME mode"] = "Avvia in modalità HOME la prossima volta",
        ["Start next time in desktop mode"] = "Avvia in modalità desktop la prossima volta",
        ["Start the game once with this core to create its native save, then close it. NX will not invent a save-index entry."] = "Avvia il gioco una volta con questo core per creare il salvataggio nativo, poi chiudilo. NX non crea voci fittizie nell'indice dei salvataggi.",
        ["Starting game"] = "Avvio del gioco",
        ["Startup animation: "] = "Animazione di avvio: ",
        ["State: "] = "Stato: ",
        ["Stop "] = "Chiudi ",
        ["Stop and back up"] = "Chiudi e crea backup",
        ["Stop and configure"] = "Chiudi e configura",
        ["Stop and verify"] = "Chiudi e verifica",
        ["Stop game"] = "Chiudi gioco",
        ["Stop game and review"] = "Chiudi gioco ed esamina",
        ["Stop this game before removing its library entry."] = "Chiudi questo gioco prima di rimuoverlo dalla raccolta.",
        ["Stop tracking this folder"] = "Non monitorare più questa cartella",
        ["Stopped"] = "Chiuso",
        ["Switch game"] = "Cambia gioco",
        ["Switch games?"] = "Cambiare gioco?",
        ["Symbols"] = "Simboli",
        ["System"] = "Sistema",
        ["Takes effect on the next game load"] = "Si applica al prossimo caricamento del gioco",
        ["The active game's profile remains in use. Its setup is deferred until the next fresh launch; other available cores can finish now."] = "Il profilo del gioco attivo è ancora in uso. La sua configurazione è rinviata al prossimo avvio; gli altri core disponibili possono completarla ora.",
        ["The core validates the actual content identity before selecting this update."] = "Il core verifica l'identità effettiva dei contenuti prima di selezionare questo aggiornamento.",
        ["The current game will close. Save your progress in the game before switching."] = "Il gioco attuale verrà chiuso. Salva i progressi nel gioco prima di cambiarlo.",
        ["The current game will stop before its content selection changes. Save in the game before continuing."] = "Il gioco attuale verrà chiuso prima di modificare la selezione dei contenuti. Salva nel gioco prima di continuare.",
        ["The current save will be preserved before replacement. Use the same title, user and core when you relaunch."] = "Il salvataggio attuale verrà conservato prima della sostituzione. Al prossimo avvio usa lo stesso gioco, utente e core.",
        ["The external source is never modified. This is a one-way reviewed import, not a live two-way merge."] = "L'origine esterna non viene mai modificata. È un'importazione verificata in una sola direzione, non una sincronizzazione bidirezionale in tempo reale.",
        ["The game did not accept this response: "] = "Il gioco non ha accettato questa risposta: ",
        ["The game requested confirmation."] = "Il gioco richiede una conferma.",
        ["The game requested native controller setup. This development frontend supports one assigned player; this applet's requested player/style setup has not been qualified and will be canceled. Return to Controls to review the actual assignment."] = "Il gioco ha richiesto la configurazione nativa del controller. Questo frontend di sviluppo supporta un giocatore assegnato; la configurazione richiesta dall'applet non è stata validata e verrà annullata. Torna a Comandi per controllare l'assegnazione attuale.",
        ["The game will close. The imported snapshot must match this native save identity. A verified copy of the current save will be preserved before replacement."] = "Il gioco verrà chiuso. La copia importata deve corrispondere all'identità di questo salvataggio nativo. Prima della sostituzione verrà conservata una copia verificata del salvataggio attuale.",
        ["The game will close. The save preserved before the last import becomes active, and the current version is backed up."] = "Il gioco verrà chiuso. Verrà ripristinato il salvataggio precedente all'ultima importazione e verrà creato un backup della versione attuale.",
        ["The launcher could not save this preference. Your previous language is still selected."] = "Il launcher non ha potuto salvare questa preferenza. La lingua precedente è ancora selezionata.",
        ["The next adventure starts here."] = "La prossima avventura inizia qui.",
        ["The pinned cores differ in their asynchronous shader and resource-flush paths. Source presence is recorded separately from a reproduced game fix."] = "I core della versione fissata differiscono nella gestione degli shader asincroni e nello svuotamento delle risorse. La presenza nel sorgente è registrata separatamente da una correzione verificata nel gioco.",
        ["The previous "] = "Il precedente ",
        ["The previous marker was archived. Select "] = "Il marcatore precedente è stato archiviato. Seleziona ",
        ["The reviewed snapshot was imported and the previous save was preserved. The receipt appears under Imported saves. Test the game before removing any backups."] = "La copia verificata è stata importata e il salvataggio precedente è stato conservato. Il riepilogo compare in Salvataggi importati. Prova il gioco prima di rimuovere i backup.",
        ["The source stays untouched. NX reads its native index on a private copy; no title or user IDs are guessed. Close the destination game's session first."] = "L'origine resta intatta. NX legge l'indice nativo su una copia privata, senza dedurre ID del gioco o dell'utente. Chiudi prima la sessione di gioco della destinazione.",
        ["There are no ready-to-use presets for this game and core. You can adjust graphics and sound in Settings."] = "Non ci sono profili pronti per questo gioco e core. Puoi regolare grafica e audio nelle Impostazioni.",
        ["These are policies included in this exact core build. The cache readouts show actual use, and captures show observed frame pacing. A 60 FPS game option still requires the correct game build, normal gameplay speed and sufficient hardware performance."] = "Questi criteri sono inclusi nella build esatta del core. I dati della cache mostrano l'uso effettivo e le registrazioni mostrano la regolarità dei fotogrammi osservata. Un'opzione a 60 FPS richiede comunque la build corretta del gioco, una velocità di gioco normale e hardware sufficientemente potente.",
        ["This action applies only to "] = "Questa azione si applica solo a ",
        ["This copies matching saves into NX Infinity. Playing elsewhere does not overwrite your newer NX progress, and changes are not written back to the other emulator."] = "Questa operazione copia i salvataggi corrispondenti in NX Infinity. Giocare altrove non sovrascrive i progressi NX più recenti e le modifiche non vengono riscritte nell'altro emulatore.",
        ["This core changed. Restore its defaults, then choose your settings again."] = "Questo core è cambiato. Ripristina i valori predefiniti e scegli di nuovo le impostazioni.",
        ["This core does not expose a selectable renderer construction mode. Its native behavior remains in use."] = "Questo core non espone una modalità di costruzione del renderer selezionabile. Resta attivo il suo comportamento nativo.",
        ["This core has no abandoned session marker. Select it and try launching again. No saves, keys or firmware were changed."] = "Questo core non ha un marcatore di sessione abbandonata. Selezionalo e prova di nuovo ad avviarlo. Salvataggi, chiavi e firmware non sono stati modificati.",
        ["This core requested an interaction that this frontend does not support: "] = "Questo core ha richiesto un'interazione non supportata dal frontend: ",
        ["This core still has an active or paused game. Save in-game first. Continuing stops that session normally before checking its marker; unsaved progress cannot be restored."] = "Questo core ha ancora un gioco attivo o in pausa. Salva prima nel gioco. Continuando, la sessione verrà chiusa normalmente prima di controllare il marcatore; i progressi non salvati non possono essere ripristinati.",
        ["This development frontend and its pinned cores require Windows and physical-device qualification. Scarlet / Violet rendering correctness, pacing, gameplay speed, animation, physics, loading, save compatibility, and sustained performance have not been established here."] = "Questo frontend di sviluppo e i core della versione fissata richiedono verifiche su Windows e dispositivi fisici. Non sono ancora state accertate correttezza grafica, regolarità dei fotogrammi, velocità di gioco, animazione, fisica, caricamento, compatibilità dei salvataggi e prestazioni prolungate in Scarlet / Violet.",
        ["This folder is not available."] = "Questa cartella non è disponibile.",
        ["This game field accepts ASCII characters."] = "Questo campo del gioco accetta caratteri ASCII.",
        ["This game field does not accept that character."] = "Questo campo del gioco non accetta quel carattere.",
        ["This game needs a native inspection. Set up local content, then inspect it to unlock title-specific core and content options."] = "Questo gioco richiede un'ispezione nativa. Configura i contenuti locali ed esaminalo per sbloccare le opzioni specifiche del core e dei contenuti.",
        ["This game · change scope"] = "Questo gioco · cambia ambito",
        ["This policy version has not been reviewed by this launcher."] = "Questa versione dei criteri non è stata esaminata dal launcher.",
        ["This removes the library record. The game file, saves and mod payloads stay in their current locations."] = "Questa azione rimuove la voce dalla raccolta. Il file del gioco, i salvataggi e i file delle mod restano nelle loro posizioni attuali.",
        ["This saves a preference for this exact core revision. Finish saving in the game before relaunching. The active renderer will not change."] = "Questa preferenza viene salvata per la revisione esatta del core. Completa il salvataggio nel gioco prima di riavviarlo. Il renderer attivo non cambia.",
        ["This view needs attention"] = "Questa vista richiede attenzione",
        ["Threaded rendering"] = "Rendering su più thread",
        ["Threaded rendering can reduce stalls while new effects compile. First-use hitches can still occur. Try the single GPU thread option if a game has compatibility problems. Changes apply on the next launch."] = "Il rendering su più thread può ridurre le pause durante la compilazione di nuovi effetti. Possono comunque verificarsi scatti al primo utilizzo. Se un gioco presenta problemi di compatibilità, prova l'opzione con un solo thread GPU. Le modifiche si applicano al prossimo avvio.",
        ["Title "] = "Gioco ",
        ["Title ID  "] = "ID del gioco  ",
        ["Toggle a container, then choose Use this DLC selection. Entitlement and native content checks still apply."] = "Attiva o disattiva un contenitore, poi scegli Usa questa selezione DLC. Restano validi i controlli sui diritti di utilizzo e sui contenuti nativi.",
        ["Truncated-cache recovery"] = "Recupero della cache troncata",
        ["Try a different title or return to all games."] = "Prova un altro titolo o torna a tutti i giochi.",
        ["Turn off"] = "Disattiva",
        ["Turn on"] = "Attiva",
        ["Type with your keyboard or click the keys below"] = "Digita con la tastiera o fai clic sui tasti qui sotto",
        ["Unavailable until this exact core revision supplies the full main executable build ID. No filename or short ID is substituted."] = "Non disponibile finché questa revisione esatta del core non fornisce l'ID completo della build dell'eseguibile principale. Non viene sostituito con un nome di file o un ID abbreviato.",
        ["Unverified"] = "Non verificato",
        ["Up one folder"] = "Cartella superiore",
        ["Update for "] = "Aggiornamento per ",
        ["Update: "] = "Aggiornamento: ",
        ["Updates and DLC"] = "Aggiornamenti e DLC",
        ["Use Add portable location to select an external Ryujinx, Canary or Kenji data folder when it is not in the usual Ryujinx location. The game and user must match exactly."] = "Usa Aggiungi percorso portatile per selezionare una cartella dati Ryujinx, Canary o Kenji esterna se non si trova nel percorso abituale di Ryujinx. Gioco e utente devono corrispondere esattamente.",
        ["Use a finite number within the core's advertised range."] = "Usa un numero finito entro l'intervallo dichiarato dal core.",
        ["Use a number within the range shown by the core, with a period for decimals."] = "Usa un numero entro l'intervallo indicato dal core, con il punto come separatore decimale.",
        ["Use one file name without directory separators."] = "Usa un nome di file senza separatori di cartelle.",
        ["Use original default"] = "Usa il valore predefinito originale",
        ["Use the default core"] = "Usa il core predefinito",
        ["Use the game's numeric keys."] = "Usa i tasti numerici del gioco.",
        ["Use this DLC selection"] = "Usa questa selezione DLC",
        ["Use this folder"] = "Usa questa cartella",
        ["Use {0}–{1} characters."] = "Usa da {0} a {1} caratteri.",
        ["User "] = "Utente ",
        ["User folder"] = "Cartella utente",
        ["Validate RTX 4060 Ti and Z1 systems separately. A successful build, protocol test, authored homebrew run or attractive screenshot does not close those gates."] = "Verifica separatamente i sistemi RTX 4060 Ti e Z1. Una compilazione riuscita, un test del protocollo, una prova homebrew o uno screenshot gradevole non completano queste verifiche.",
        ["Validation could not finish"] = "Impossibile completare la verifica",
        ["Verified backups"] = "Backup verificati",
        ["Version "] = "Versione ",
        ["View game"] = "Visualizza gioco",
        ["Waiting for the game…"] = "In attesa del gioco…",
        ["Welcome home"] = "Benvenuto",
        ["Which emulator uses this folder?"] = "Quale emulatore usa questa cartella?",
        ["Window and event scheduling"] = "Gestione di finestre ed eventi",
        ["Windowed"] = "In finestra",
        ["Your favorite games, all in one place."] = "I tuoi giochi preferiti, tutti in un unico posto.",
        ["Your games, together."] = "I tuoi giochi, insieme.",
        ["Your library"] = "La tua raccolta",
        ["Your next adventure starts here"] = "La prossima avventura inizia qui",
        ["Your source is retained once. Each installed core reports whether it supports and validates that source."] = "L'origine viene conservata una sola volta. Ogni core installato indica se la supporta e la verifica.",
        ["ZIP package or XCI update source"] = "Pacchetto ZIP o origine di aggiornamento XCI",
        ["firmware source"] = "origine del firmware",
        ["keys"] = "chiavi",
        ["native flags read; transition confirmation unavailable"] = "stato nativo letto; conferma del passaggio non disponibile",
        ["not measured"] = "non misurato",
        ["not observed"] = "non osservato",
        ["not reported"] = "non riportato",
        ["not stored"] = "non memorizzato",
        ["observed"] = "osservato",
        ["reported"] = "riportato",
        ["stored"] = "memorizzato",
        ["transition confirmed"] = "passaggio confermato",
        ["transition not confirmed"] = "passaggio non confermato",
        ["unavailable"] = "non disponibile",
        ["unknown"] = "sconosciuto",
        ["unqualified"] = "non validato",
        ["{0} connected"] = "{0} collegati",
        ["{0} matching entries · Local files stay in their original location"] = "{0} elementi corrispondenti · I file locali restano nella posizione originale",
        ["{0} × {1} presentation pixels"] = "{0} × {1} pixel di presentazione",
        ["{0} × {1} window units"] = "{0} × {1} unità della finestra",
        ["The first scan starts when the launcher opens."] = "La prima scansione inizia all'apertura del launcher.",
        ["No scan completed yet."] = "Nessuna scansione completata.",
        ["{0} possible save(s) found · Last scan {1:t}"] = "{0} possibili salvataggi trovati · Ultima scansione {1:t}",
        [" · {0} imported in the latest attempt"] = " · {0} importati nell'ultimo tentativo",
        [" · {0} portable location(s)"] = " · {0} percorsi portatili",
        ["Waiting for a scan"] = "In attesa di una scansione",
        ["Automatic import is off"] = "Importazione automatica disattivata",
        ["Scanned · import has not been requested"] = "Scansione completata · importazione non richiesta",
        ["No supported source saves found"] = "Nessun salvataggio di origine supportato trovato",
        ["This save format is not supported yet"] = "Questo formato di salvataggio non è ancora supportato",
        ["Waiting for the loaded game to close"] = "In attesa della chiusura del gioco caricato",
        ["No existing empty save destination"] = "Nessuna destinazione di salvataggio vuota esistente",
        ["Existing saves preserved · no empty match"] = "Salvataggi esistenti conservati · nessuna destinazione vuota corrispondente",
        ["Too many destinations for one import"] = "Troppe destinazioni per una sola importazione",
        ["Native verification needs attention"] = "La verifica nativa richiede attenzione",
        ["Import review complete"] = "Verifica dell'importazione completata",
        ["Import rolled back"] = "Importazione annullata e stato precedente ripristinato",
        ["Review the import results"] = "Controlla i risultati dell'importazione",
        ["Latest scan and per-core results"] = "Ultima scansione e risultati per core",
        ["Imported saves and guarded rollback"] = "Salvataggi importati e ripristino protetto",
        ["Manage portable locations"] = "Gestisci percorsi portatili",
        ["Locations checked"] = "Percorsi controllati",
        ["{0} possible save(s)"] = "{0} possibili salvataggi",
        ["Possible saves"] = "Possibili salvataggi",
        ["Title not verified"] = "Titolo non verificato",
        ["Revision: "] = "Revisione: ",
        ["Imported in this attempt: "] = "Importati in questo tentativo: ",
        ["Latest attempt"] = "Ultimo tentativo",
        ["Location {0}"] = "Percorso {0}",
        ["Title: "] = "Titolo: ",
        ["Core: "] = "Core: ",
        ["Imported from: "] = "Importato da: ",
        ["Format: "] = "Formato: ",
        ["Receipt: "] = "Ricevuta: ",
        ["✓ Selected: native keyboard / no frontend gamepad"] = "✓ Selezionato: tastiera nativa / nessun controller del frontend"
    };

    private static LocalTranslator? _local;

    private static readonly ConcurrentDictionary<string, byte> Eligible = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);

    private static readonly ConcurrentDictionary<string, string> Translated = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);

    private static readonly ConcurrentQueue<(WeakReference<Control> Control, string Text, string Language, LocalTranslator Service)> Pending = new ConcurrentQueue<(WeakReference<Control>, string, string, LocalTranslator)>();

    private static int _pumping;

    public static string Language { get; private set; } = "en";

    public static IReadOnlyList<(string Id, string Name)> Languages { get; } = Array.AsReadOnly(new (string, string)[50]
    {
        ("en", "English"),
        ("it", "Italiano"),
        ("es", "Español"),
        ("fr", "Français"),
        ("de", "Deutsch"),
        ("pt", "Português"),
        ("sq", "Albanian"),
        ("ar", "Arabic"),
        ("az", "Azerbaijani"),
        ("eu", "Basque"),
        ("bn", "Bengali"),
        ("bg", "Bulgarian"),
        ("ca", "Catalan"),
        ("zt", "Chinese (traditional)"),
        ("zh", "Chinese"),
        ("cs", "Czech"),
        ("da", "Danish"),
        ("nl", "Dutch"),
        ("eo", "Esperanto"),
        ("et", "Estonian"),
        ("fi", "Finnish"),
        ("gl", "Galician"),
        ("el", "Greek"),
        ("he", "Hebrew"),
        ("hi", "Hindi"),
        ("hu", "Hungarian"),
        ("id", "Indonesian"),
        ("ga", "Irish"),
        ("ja", "Japanese"),
        ("ko", "Korean"),
        ("ky", "Kyrgyz"),
        ("lv", "Latvian"),
        ("lt", "Lithuanian"),
        ("ms", "Malay"),
        ("nb", "Norwegian"),
        ("fa", "Persian"),
        ("pl", "Polish"),
        ("pb", "Portuguese (Brazil)"),
        ("ro", "Romanian"),
        ("ru", "Russian"),
        ("sk", "Slovak"),
        ("sl", "Slovenian"),
        ("sw", "Swahili"),
        ("sv", "Swedish"),
        ("tl", "Tagalog"),
        ("th", "Thai"),
        ("tr", "Turkish"),
        ("uk", "Ukrainian"),
        ("ur", "Urdu"),
        ("vi", "Vietnamese")
    });

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> BuildAdditional()
    {
        string[] array = new string[4] { "es", "fr", "de", "pt" };
        Dictionary<string, Dictionary<string, string>> dictionary = array.ToDictionary((string id) => id, (string _) => new Dictionary<string, string>(StringComparer.Ordinal));
        string[] array2 = "Play|Jugar|Jouer|Spielen|Jogar\nVersion|Versión|Version|Version|Versão\nCore|Núcleo|Cœur|Core|Núcleo\nNot identified|Sin identificar|Non identifié|Nicht erkannt|Não identificado\nReading mod inventory…|Leyendo mods…|Lecture des mods…|Mods werden gelesen…|Lendo mods…\nMods: game build not identified|Mods: versión del juego sin identificar|Mods : version du jeu non identifiée|Mods: Spiel-Build nicht erkannt|Mods: versão do jogo não identificada\nNX mods: {0} installed · {1} enabled|Mods de NX: {0} instalados · {1} activos|Mods NX : {0} installés · {1} actifs|NX-Mods: {0} installiert · {1} aktiv|Mods do NX: {0} instalados · {1} ativos\nMod inventory unavailable|Inventario de mods no disponible|Inventaire des mods indisponible|Mod-Liste nicht verfügbar|Inventário de mods indisponível\nResume|Continuar|Reprendre|Fortsetzen|Continuar\nLibrary|Biblioteca|Bibliothèque|Bibliothek|Biblioteca\nSettings|Ajustes|Paramètres|Einstellungen|Configurações\nSearch|Buscar|Rechercher|Suchen|Pesquisar\nConfigure|Configurar|Configurer|Konfigurieren|Configurar\nMods|Mods|Mods|Mods|Mods\nSaves|Partidas guardadas|Sauvegardes|Spielstände|Dados salvos\nUpdates|Actualizaciones|Mises à jour|Updates|Atualizações\nUpdates & DLC|Actualizaciones y DLC|Mises à jour et DLC|Updates und DLC|Atualizações e DLC\nContent|Contenido|Contenu|Inhalte|Conteúdo\nController|Mando|Manette|Controller|Controle\nControllers|Mandos|Manettes|Controller|Controles\nGraphics|Gráficos|Graphismes|Grafik|Gráficos\nAudio|Audio|Audio|Audio|Áudio\nSystem|Sistema|Système|System|Sistema\nInput|Entrada|Commandes|Eingabe|Entrada\nPerformance|Rendimiento|Performances|Leistung|Desempenho\nDiagnostics|Diagnóstico|Diagnostic|Diagnose|Diagnóstico\nImprovements|Mejoras|Améliorations|Verbesserungen|Melhorias\nOpen Folder|Abrir carpeta|Ouvrir le dossier|Ordner öffnen|Abrir pasta\nCreate Shortcut|Crear acceso directo|Créer un raccourci|Verknüpfung erstellen|Criar atalho\nCreate Desktop Shortcut|Crear acceso directo en el escritorio|Créer un raccourci sur le bureau|Desktop-Verknüpfung erstellen|Criar atalho na área de trabalho\nExport Diagnostics|Exportar diagnóstico|Exporter le diagnostic|Diagnose exportieren|Exportar diagnóstico\nChoose a core|Elegir núcleo|Choisir un cœur|Core auswählen|Escolher núcleo\nChoose a game|Elegir juego|Choisir un jeu|Spiel auswählen|Escolher jogo\nSelect a game|Selecciona un juego|Sélectionner un jeu|Spiel auswählen|Selecione um jogo\nAdd game folder|Añadir carpeta de juegos|Ajouter un dossier de jeux|Spieleordner hinzufügen|Adicionar pasta de jogos\nShow all games|Mostrar todos los juegos|Afficher tous les jeux|Alle Spiele anzeigen|Mostrar todos os jogos\nReview files|Revisar archivos|Examiner les fichiers|Dateien prüfen|Revisar arquivos\nOverview|Descripción|Présentation|Übersicht|Visão geral\nDetails|Detalles|Détails|Details|Detalhes\nAdded|Añadido|Ajouté|Hinzugefügt|Adicionado\nMore|Más|Plus|Mehr|Mais\nBack|Atrás|Retour|Zurück|Voltar\nCancel|Cancelar|Annuler|Abbrechen|Cancelar\nClose|Cerrar|Fermer|Schließen|Fechar\nSave|Guardar|Enregistrer|Speichern|Salvar\nApply|Aplicar|Appliquer|Übernehmen|Aplicar\nRemove|Eliminar|Supprimer|Entfernen|Remover\nImport|Importar|Importer|Importieren|Importar\nEnable|Activar|Activer|Aktivieren|Ativar\nDisable|Desactivar|Désactiver|Deaktivieren|Desativar\nRefresh|Actualizar|Actualiser|Aktualisieren|Atualizar\nLanguage|Idioma|Langue|Sprache|Idioma\nLauncher language|Idioma del lanzador|Langue du lanceur|Launcher-Sprache|Idioma do iniciador\nChoose the launcher language|Elegir idioma del lanzador|Choisir la langue du lanceur|Launcher-Sprache auswählen|Escolher idioma do iniciador\nLanguage for NX Infinity. Games keep their own language settings.|Idioma de NX Infinity. Los juegos mantienen sus propios ajustes de idioma.|Langue de NX Infinity. Les jeux conservent leurs propres paramètres de langue.|Sprache für NX Infinity. Spiele behalten ihre eigenen Spracheinstellungen.|Idioma do NX Infinity. Os jogos mantêm suas próprias configurações de idioma.\nTheme|Tema|Thème|Design|Tema\nThemes|Temas|Thèmes|Designs|Temas\nFavorite|Favorito|Favori|Favorit|Favorito\nFavorites|Favoritos|Favoris|Favoriten|Favoritos\nOriginal|Original|D'origine|Original|Original\nResolution|Resolución|Résolution|Auflösung|Resolução\nFrame rate|Fotogramas por segundo|Fréquence d'images|Bildrate|Taxa de quadros\nGraphics API|API gráfica|API graphique|Grafikschnittstelle|API gráfica\nAnti-aliasing|Suavizado de bordes|Anticrénelage|Kantenglättung|Suavização de bordas\nAnisotropic filtering|Filtrado anisotrópico|Filtrage anisotrope|Anisotrope Filterung|Filtragem anisotrópica\nShader cache|Caché de sombreadores|Cache des shaders|Shader-Cache|Cache de shaders\nGPU selection|Selección de GPU|Choix du GPU|GPU-Auswahl|Seleção de GPU\nStop game|Detener juego|Arrêter le jeu|Spiel beenden|Parar jogo\nStarting game|Iniciando juego|Démarrage du jeu|Spiel wird gestartet|Iniciando jogo\nCancel launch|Cancelar inicio|Annuler le démarrage|Start abbrechen|Cancelar início\nLast issue|Último problema|Dernier problème|Letztes Problem|Último problema\nRead active values|Leer valores activos|Lire les valeurs actives|Aktive Werte lesen|Ler valores ativos\nSearch settings|Buscar ajustes|Rechercher un paramètre|Einstellungen suchen|Pesquisar configurações\nAdd update|Añadir actualización|Ajouter une mise à jour|Update hinzufügen|Adicionar atualização\nAdd DLC|Añadir DLC|Ajouter un DLC|DLC hinzufügen|Adicionar DLC\nBackup|Copia de seguridad|Sauvegarde|Sicherung|Backup\nRestore|Restaurar|Restaurer|Wiederherstellen|Restaurar\nOpen Logs|Abrir registros|Ouvrir les journaux|Protokolle öffnen|Abrir registros\nRun Health Check|Comprobar estado|Vérifier l'état|Systemprüfung starten|Verificar integridade\nRepair Installation|Reparar instalación|Réparer l'installation|Installation reparieren|Reparar instalação".Split('\n', StringSplitOptions.RemoveEmptyEntries);
        for (int num = 0; num < array2.Length; num++)
        {
            string[] array3 = array2[num].TrimEnd('\r').Split('|');
            if (array3.Length != 5)
            {
                throw new InvalidDataException("Invalid launcher translation row.");
            }
            for (int num2 = 0; num2 < array.Length; num2++)
            {
                dictionary[array[num2]].Add(array3[0], array3[num2 + 1]);
            }
        }
        return ((IEnumerable<KeyValuePair<string, Dictionary<string, string>>>)dictionary).ToDictionary((Func<KeyValuePair<string, Dictionary<string, string>>, string>)((KeyValuePair<string, Dictionary<string, string>> pair) => pair.Key), (Func<KeyValuePair<string, Dictionary<string, string>>, IReadOnlyDictionary<string, string>>)((KeyValuePair<string, Dictionary<string, string>> pair) => pair.Value));
    }

    public static string NormalizeLanguage(string? language)
    {
        string id = ((language != null) ? language.Trim().ToLowerInvariant().Split('-', '_')[0] : null) ?? "en";
        if (!Languages.Any(((string Id, string Name) item) => item.Id == id))
        {
            return "en";
        }
        return id;
    }

    public static void SetLanguage(string? language)
    {
        Language = NormalizeLanguage(language);
    }

    public static string Get(string english)
    {
        IReadOnlyDictionary<string, string> readOnlyDictionary;
        if (!(Language == "it"))
        {
            IReadOnlyDictionary<string, string> valueOrDefault = Additional.GetValueOrDefault(Language);
            readOnlyDictionary = valueOrDefault;
        }
        else
        {
            readOnlyDictionary = Italian;
        }
        IReadOnlyDictionary<string, string> readOnlyDictionary2 = readOnlyDictionary;
        if (readOnlyDictionary2 != null && readOnlyDictionary2.TryGetValue(english, out var value))
        {
            return value;
        }
        int length = english.Length;
        if (length > 1 && length <= 4000 && Eligible.Count < 12000)
        {
            Eligible.TryAdd(english, 0);
        }
        LocalTranslator? local = _local;
        if (local == null || !local.Preferences.TranslateLauncher || !Translated.TryGetValue(Language + ":" + english, out string value2))
        {
            return english;
        }
        return value2;
    }

    public static string Format(string english, params object?[] arguments)
    {
        CultureInfo provider;
        try
        {
            provider = CultureInfo.GetCultureInfo(Language);
        }
        catch (CultureNotFoundException)
        {
            provider = CultureInfo.InvariantCulture;
        }
        return string.Format(provider, Get(english), arguments);
    }

    public static string LanguageName(string? language)
    {
        return Languages.First(((string Id, string Name) item) => item.Id == NormalizeLanguage(language)).Name;
    }

    public static void ConfigureLocal(LocalTranslator service)
    {
        _local = service;
    }

    public static void ReleaseLocal(LocalTranslator service)
    {
        if (_local == service)
        {
            _local = null;
        }
    }

    internal static void TranslateLater(Control control, string source)
    {
        LocalTranslator local = _local;
        if (local != null && local.Preferences.TranslateLauncher && !(Language == "en") && Eligible.ContainsKey(source) && local.HasPair("en", Language) && Pending.Count < 1024)
        {
            Pending.Enqueue((new WeakReference<Control>(control), source, Language, local));
            if (Interlocked.CompareExchange(ref _pumping, 1, 0) == 0)
            {
                _ = PumpTranslationsAsync();
            }
        }
    }

    private static async Task PumpTranslationsAsync()
    {
        _ = 1;
        try
        {
            while (!Pending.IsEmpty)
            {
                await Task.Delay(100);
                List<(WeakReference<Control>, string, string, LocalTranslator)> list = new List<(WeakReference<Control>, string, string, LocalTranslator)>();
                (WeakReference<Control>, string, string, LocalTranslator) result;
                while (list.Count < 32 && Pending.TryDequeue(out result))
                {
                    list.Add(result);
                }
                foreach (IGrouping<(string, LocalTranslator), (WeakReference<Control>, string, string, LocalTranslator)> batch in list.GroupBy(((WeakReference<Control> Control, string Text, string Language, LocalTranslator Service) x) => (Language: x.Language, Service: x.Service)))
                {
                    string[] texts = batch.Select(((WeakReference<Control> Control, string Text, string Language, LocalTranslator Service) x) => x.Text).Distinct().ToArray();
                    try
                    {
                        string[] array = await batch.Key.Item2.TranslateBatchAsync(texts, "en", batch.Key.Item1);
                        for (int num = 0; num < texts.Length; num++)
                        {
                            Translated[batch.Key.Item1 + ":" + texts[num]] = array[num];
                        }
                        Dispatcher.UIThread.Post(() =>
                        {
                            if (Language != batch.Key.Item1 || _local != batch.Key.Item2 || !_local.Preferences.TranslateLauncher)
                            {
                                return;
                            }
                            foreach (var item in batch)
                            {
                                if (item.Item1.TryGetTarget(out var target) && Translated.TryGetValue(item.Item3 + ":" + item.Item2, out string value))
                                {
                                    if (target is TextBlock textBlock && textBlock.Text == item.Item2)
                                    {
                                        textBlock.Text = value;
                                    }
                                    else if (target is Button { Content: string content } button && content == item.Item2)
                                    {
                                        button.Content = value;
                                    }
                                }
                            }
                        });
                    }
                    catch (Exception ex) when ((ex is IOException || ex is InvalidOperationException || ex is OperationCanceledException || ex is Win32Exception || ex is JsonException) ? true : false)
                    {
                    }
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _pumping, 0);
            if (!Pending.IsEmpty && Interlocked.CompareExchange(ref _pumping, 1, 0) == 0)
            {
                _ = PumpTranslationsAsync();
            }
        }
    }
}
