# SkyRoof

Please see:

- [SkyRoof upstream Website](https://ve3nea.github.io/SkyRoof)
- [SkyRoof fork documentation (GitHub Pages)](https://iu-yang1.github.io/SkyRoof/)
- [Fork extensions — English](https://iu-yang1.github.io/SkyRoof/fork-guide/overview.html)
- [Fork 扩展功能 — 简体中文](https://iu-yang1.github.io/SkyRoof/zh-cn/fork-guide/overview.html)
- [Documentation publishing history (Actions)](https://github.com/Iu-yang1/SkyRoof/actions/workflows/deploy-docfx.yml)
- [SkyRoof Discussion Group](https://groups.google.com/g/skyroof)

<center><br><br>

![SkyRoof](docs/images/skyroof_icon.png)

</center>

## Fork extensions

This fork is based on upstream v1.55 and adds custom orbit sources and JPL
ephemerides, persistent Base corrections and editable satellite transmitters,
extended SkyCAT/RS-BA1 and IC-9700 spectrum integration, feedback-verified
multi-waypoint rotator PARK, theme variants and Windows audio/RF-gain controls.

See the bilingual [fork documentation](https://iu-yang1.github.io/SkyRoof/). The [DocFX publishing workflow](https://github.com/Iu-yang1/SkyRoof/actions/workflows/deploy-docfx.yml) provides a reliable deployment-history entry even if GitHub's optional Deployments dashboard is unavailable.

The original upstream website does not necessarily document or ship these features.
The IC-9700 independent Direct LAN scope source remains experimental.

### GitHub Pages publishing (site 404 troubleshooting)

The Docs workflow builds the DocFX HTML and **deploys it through GitHub's official Pages API**.
For initial setup, an admin must open [Settings → Pages](https://github.com/Iu-yang1/SkyRoof/settings/pages)
and select **Build and deployment → Source: GitHub Actions**. Then run
[Deploy DocFX Site to GitHub Pages](https://github.com/Iu-yang1/SkyRoof/actions/workflows/deploy-docfx.yml)
from `master`. A successful build alone, or a historical push to `gh-pages`,
does **not** establish that the live website was published. The deploy job's
`github-pages` environment links to the actual Pages site after publication.


## Building The Solution

To build SkyRoof, open the solution in Visual Studio 2026 and click on Build in the menu.
