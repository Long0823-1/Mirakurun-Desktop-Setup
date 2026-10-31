# Third-party software

This setup program is MIT licensed. Downloaded packages retain their own licenses and notices; they are not relicensed by this project. Packages are downloaded directly from their publishers and verified using catalog.json. All files under the selected x64 package directory, including notices, are retained.

| Component | Version / distribution | Source and license |
| --- | --- | --- |
| BonDriver_Mirakurun | v2.0 x64 | https://github.com/Chinachu/BonDriver_Mirakurun/tree/v2.0 — MIT |
| TVTest | 0.10.0-260730, DTV-Builds x64 | https://github.com/DBCTRADO/TVTest and https://github.com/tsukumijima/TVTest — GPL-2.0; see package notices for bundled components |
| EDCB / EPGTimer | EDCB-260921, DTV-Builds x64 | https://github.com/tsukumijima/EDCB — original EDCB freeware terms in Document/Readme.txt (not GPL); commercial incorporation requires permission. Additional files retain their individual terms. |
| Microsoft Visual C++ Runtime | catalog.json pinned official installer | Microsoft proprietary redistributable terms; installed only when missing and requested |

DTV-Builds distribution: https://github.com/tsukumijima/DTV-Builds/tree/8fa812a90df79f6d8f19d821ebe72b7f8c01014c . Consult each package's build instructions and Readme for source branches and included plugins / decoders. This repository and release do not embed these packages.

The BonDriver interface declaration in native/IBonDriver2.h is from https://github.com/Long0823-1/TCLTV-Bondriver (MIT). The native probing implementation in this repository is original code and is embedded in the setup executable.
