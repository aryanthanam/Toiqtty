toiqtty Browser Companion 0.4.0

Chromium browsers (Edge, Chrome, Brave, Vivaldi, and compatible browsers):
  1. Open the browser's Extensions page and enable Developer mode.
  2. Choose Load unpacked and select the chromium folder next to this file.
  3. Install separately in each browser profile you want to connect.

Firefox and compatible browsers:
  Open about:debugging#/runtime/this-firefox, choose Load Temporary Add-on,
  and select firefox/manifest.json. Temporary installs disappear on restart.
  Permanent installation on standard Firefox requires Mozilla signing;
  no signed store package is included in this development release.

In toiqtty, choose Browser > Profiles and cookie sharing > Connect another
browser profile. Paste the pairing code into this extension's popup and name
the profile. Back in toiqtty select that connection, list the site origins,
and Save site selection. Reopen the popup and Approve sites and sync now.
Choose the connected profile as toiqtty's default, then create a browser pane.
Existing panes keep the profile with which they were created; use a pane's
menu > Change browser profile to switch and reload it.

Cookies flow from the source browser into that source's separate toiqtty
WebView2 profile. A full snapshot replaces that site's matching cookies in
toiqtty, including source-side deletions. Changes made inside toiqtty are not
written back. Automatic sync runs while both browser and toiqtty are open.
You can choose a Firefox container in the popup; private cookie stores are
excluded. Switching stores replaces cookies for all configured sites.
Cookies and extension site permissions apply to a host across ports; listing
localhost:3000 does not isolate its cookies from localhost:8080.

No passwords, history, localStorage, IndexedDB, extension data, partitioned
cookies, or device-bound session keys are transferred. Some sites require a
fresh sign-in inside toiqtty even after cookies are copied. Cookie values are
never logged or written to toiqtty configuration files. Connection tokens are
protected with Windows DPAPI in toiqtty and kept in extension-local storage.
The bridge binds to 127.0.0.1. Every request is signed with the connection token
without sending it, and the extension shares nothing with an endpoint that
cannot prove it holds the same token.
Each pairing code expires in ten minutes and can be used only once.

Disconnect a profile in toiqtty to revoke its token. Previously copied cookies
remain in WebView2 until cleared from the browser pane's menu. Removing a site
stops future sharing; clear its existing cookies if you also want to sign out.
