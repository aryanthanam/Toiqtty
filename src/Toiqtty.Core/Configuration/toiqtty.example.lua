local toiqtty = require('toiqtty')
local config = toiqtty.config_builder()

-- Lua functions, conditions, loops, and local require() modules work here.
local large_text = toiqtty.getenv('TOIQTTY_LARGE_TEXT') == '1'
config.font_size = large_text and 18 or 14
config.font = 'JetBrainsMono Nerd Font Mono'
config.shell = toiqtty.default_shell
config.start_directory = toiqtty.home_dir
config.ligatures = true
config.scrollback_limit_bytes = 50000000 -- Ghostty's default, per terminal.
config.scrollback_limit_lines = 'unlimited'
config.scrolling = { lines_per_notch = 3, app_events_per_notch = 1, reverse = false }
config.desktop_notifications = true
config.keybindings = { newBrowser = 'Ctrl+Shift+U' }

-- The bottom status bar is hidden by default. Enable and customize it here.
config.status_bar = {
  enabled = false,
  left = { '{session}', '{directory}', '{branch}' },
  right = { '{panes} panes', '{font_size}px' },
  separator = '  ·  ',
  height = 24,
  font_size = 11,
  -- background = '#131519', foreground = '#D7DBE2', -- Otherwise follow the session theme.
}

config.browser = {
  home_page = 'about:blank',
  profile = 'independent', -- Or a connected browser profile's name or ID.
  open_terminal_links = true,
}

-- Run this layout on cold startup, including after a reboot or host exit.
-- Previous layouts on disk are not replayed. Reopening a window while its
-- shell host is alive reconnects those sessions without running this again.
config.startup = {
  mode = 'when_empty',
  sessions = {
    {
      name = 'Development',
      theme = 'Cobalt2', -- A bundled preset for the session UI and its terminal panes.
      tabs = {
        {
          name = 'Code and preview',
          root = {
            split = 'right', ratio = 0.6,
            first = { shell = toiqtty.default_shell, cwd = toiqtty.home_dir },
            second = {
              split = 'down',
              first = { kind = 'browser', url = 'http://localhost:3000' },
              second = { shell = toiqtty.default_shell },
            },
          },
        },
        { name = 'Logs', shell = toiqtty.default_shell },
      },
    },
  },
}

return config
