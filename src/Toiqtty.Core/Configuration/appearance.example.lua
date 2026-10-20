-- Complete appearance example; copy the parts you want into toiqtty.lua.
local oro = require('toiqtty')
local config = oro.config_builder()

config.font = 'JetBrainsMono Nerd Font Mono'
config.font_size = 10.333 -- Roughly 7.75 points in WezTerm; toiqtty uses logical pixels.
config.font_weight = 'Medium'
config.cursor = { style = 'block', blink = true, blink_interval = 650 }

config.color_schemes = {
  ['Personal Mocha'] = {
    foreground = '#CDD6F4', background = '#1F1F28', accent = '#74C7EC',
    cursor = '#F5E0DC', cursor_foreground = '#11111B',
    selection_background = '#585B70', selection_foreground = '#CDD6F4',
    ansi = { '#0C0C0C', '#C50F1F', '#13A10E', '#C19C00', '#0037DA', '#881798', '#3A96DD', '#CCCCCC' },
    brights = { '#767676', '#E74856', '#16C60C', '#F9F1A5', '#3B78FF', '#B4009E', '#61D6D6', '#F2F2F2' },
    indexed = { [16] = '#FAB387', [17] = '#F5E0DC' },
  },
}
config.color_scheme = 'Personal Mocha' -- Explicit session themes take precedence.

-- Reuse existing images without copying them into the application.
local backdrops = oro.path_join(oro.home_dir, '.config/wezterm/backdrops')
if oro.directory_exists(backdrops) then
  config.wallpaper = { directory = backdrops, opacity = 0.04, fit = 'cover', random = true }
end
config.keybindings = {
  randomWallpaper = 'Alt+F6', previousWallpaper = 'Alt+F7', nextWallpaper = 'Alt+F8',
  chooseWallpaper = 'Alt+F9', toggleWallpaper = 'Alt+B',
}

config.status_bar = { enabled = false } -- Set true to display format_status output.
config.events = {
  format_tab_title = function(tab)
    return {{
      text = (tab.is_active and '● ' or '') .. tab.name,
      foreground = tab.is_active and '#74C7EC' or '#BAC2DE',
      bold = tab.is_active,
    }}
  end,
  format_status = function(ctx)
    local battery = ctx.battery_percent and string.format('  %d%%', ctx.battery_percent) or ''
    return {
      left = {{ text = ctx.session .. ' · ' .. ctx.tab, foreground = '#CDD6F4' }},
      right = {{ text = oro.strftime('%a %H:%M:%S') .. battery, foreground = '#FAB387' }},
    }
  end,
}

return config
