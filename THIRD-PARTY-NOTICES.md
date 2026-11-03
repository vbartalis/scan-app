# Third-party notices

The scanner is driven through SICK's GenIStream SDK. The files it needs are bundled, unmodified, in [`lib/sick/`](lib/sick/) so the repository builds without installing anything from SICK. They were copied from SICK Stream Setup (SDK and firmware release 4.3.0).

| Files in `lib/sick/` | Component | Licence | Licence text |
|---|---|---|---|
| `Sick.GenIStreamDotNet.dll`, `GenIStreamDotNetInterop.dll`, `SICKGigEVisionTL.cti` | SICK GenIStream (.NET wrapper, interop, GigE Vision transport layer), © SICK AG | MIT | [`SICK-MIT.txt`](lib/sick/licenses/SICK-MIT.txt) |
| `GCBase_MD_VC120_v3_1.dll`, `GenApi_MD_VC120_v3_1.dll`, `NodeMapData_MD_VC120_v3_1.dll`, `XmlParser_MD_VC120_v3_1.dll`, `Log_MD_VC120_v3_1.dll` | GenICam runtime 3.1, © EMVA and contributors | GenICam runtime licence (BSD-style) | [`GenICam.txt`](lib/sick/licenses/GenICam.txt) |
| `log4cpp_MD_VC120_v3_1.dll`, `MathParser_MD_VC120_v3_1.dll` | log4cpp (<http://log4cpp.sourceforge.net>) and Mathparser (<http://kirya.narod.ru/mathparser.html>), shipped with the GenICam runtime | LGPL 2.1 | [`LGPL-2.1.txt`](lib/sick/licenses/LGPL-2.1.txt) |
| `libeay32MD.dll`, `ssleay32MD.dll` | OpenSSL | OpenSSL and SSLeay licences | [`OpenSSL.txt`](lib/sick/licenses/OpenSSL.txt), [`SSLeay.txt`](lib/sick/licenses/SSLeay.txt) |

**On the SICK licence.** No licence file ships inside Stream Setup. The terms used here come from SICK's own product page for the Stream SDK, which lists its licence type as "MIT License": <https://www.sick.com/jp/en/catalog/products/digital-services-and-software/engineering-tools/sick-stream-software/stream-sdk/p/p685756?tab=detail>. If you have the Stream SDK download from support.sick.com, its own terms take precedence.

**On log4cpp and Mathparser.** Each is a separate, unmodified dynamic library; you may replace it with your own build of the same version. Their sources are at the links above.

**Not bundled: the Microsoft Visual C++ runtimes.** The native files above link against the Visual C++ 2013 (x64) runtime (`MSVCR120.dll`, `MSVCP120.dll`) and the Visual C++ 2015–2022 (x64) runtime (`VCRUNTIME140.dll`, `MSVCP140.dll`). They are not in this repository; install Microsoft's redistributables to use a real scanner.

This product includes software developed by the OpenSSL Project for use in the OpenSSL Toolkit (http://www.openssl.org/). This product includes cryptographic software written by Eric Young (eay@cryptsoft.com).

The GenICam and OpenSSL texts are copied unchanged from Stream Setup's `Third Party Licenses` folder. Stream Setup's OpenSSL file omits the original SSLeay licence, so `SSLeay.txt` is taken from OpenSSL's own 1.0.2 `LICENSE`; `LGPL-2.1.txt` is the text published by the FSF.
