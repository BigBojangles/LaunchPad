Put both PNGs in the repo's docs/ folder, then replace the top of README.md
(the old <p><img launchpad-lockup.png></p>, the "# LaunchPad" line, and the bold slogan line) with this:

<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="docs/launchpad-header-dark.png">
    <img src="docs/launchpad-header-light.png" alt="LaunchPad. Put the agent in a box. Watch it work." width="560">
  </picture>
</p>

<p align="center">
  <img src="https://img.shields.io/badge/platform-Windows-0078D6" alt="Windows">
  <img src="https://img.shields.io/badge/license-MIT-green" alt="MIT">
  <img src="https://img.shields.io/badge/status-pre--beta-B04614" alt="pre-beta">
</p>

<!-- When the demo exists: drag the .mp4 into the README editor on github.com and paste the link it gives you here. It plays inline. -->

The 1280x640 hero card (Logo\hero\launchpad-social-dark.png) goes in repo Settings > General > Social preview, so links to the repo show it.
