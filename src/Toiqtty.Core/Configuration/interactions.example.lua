-- Optional controls. Merge the fields you want into your existing toiqtty.lua.
local oro = require('toiqtty')
local config = oro.config_builder()

config.keybindings = {
  switchSession = 'Ctrl+Shift+K',
  randomWallpaper = 'Alt+/', previousWallpaper = 'Alt+,', nextWallpaper = 'Alt+.',
  chooseWallpaper = 'Ctrl+Alt+/', toggleWallpaper = 'Alt+B',
  toggleFullscreen = 'F11', toggleMaximized = 'Alt+Enter',
}

config.leader = { key = 'Ctrl+Alt+Space', timeout_ms = 1500 }
config.bindings = {
  { key = 'Shift+Enter', action = { send_text = '\27\r' } },
  { key = 'Alt+F6', action = 'randomWallpaper' }, -- Additional shortcut for an existing action.
  { key = 'LEADER+R', action = { activate_key_table = 'resize' } },
  { key = 'LEADER+F', action = { activate_key_table = 'font' } },
  { key = 'LEADER+N', action = { sequence = { 'newTab', { paste = 'Get-Location' } } } },
  { key = 'LEADER+S', action = { lua = 'scratch' } },
}
config.key_tables = {
  resize = { timeout_ms = 3000, bindings = {
    { key = 'H', action = 'resizeLeft' }, { key = 'J', action = 'resizeDown' },
    { key = 'K', action = 'resizeUp' }, { key = 'L', action = 'resizeRight' },
    { key = 'Enter', action = { pop_key_table = true } },
  } },
  font = { timeout_ms = 3000, bindings = {
    { key = 'J', action = 'increaseFontSize' }, { key = 'K', action = 'decreaseFontSize' },
    { key = 'R', action = 'resetFontSize' }, { key = 'Enter', action = { pop_key_table = true } },
  } },
}
config.actions = {
  scratch = function(ctx)
    return { sequence = { 'newTab', { paste = '# Scratch in ' .. ctx.session } } }
  end,
}

config.appearance = {
  padding = { left = 15, right = 15, top = 10, bottom = 8 },
  line_height = 1.5, cell_width = 1.0,
  inactive_pane_opacity = 0.75,
  font_features = { liga = 1, calt = 1 }, -- Four-character OpenType feature tags.
}
config.window = {
  width = 1280, height = 820, state = 'normal', always_on_top = false,
  backdrop = 'acrylic', background_opacity = 0.85,
}
-- Optional backdrop choices: none (solid), mica, mica_alt, acrylic.
-- Opacity reveals the selected Windows material while glyphs stay opaque.
-- Wallpapers retain their independent image opacity and cycling controls.
return config
