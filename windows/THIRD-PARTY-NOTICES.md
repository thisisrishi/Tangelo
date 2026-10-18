# Third-party notices (Windows build)

Tangelo for Windows is MIT-licensed and builds on these components:

| Component | License | Use |
|---|---|---|
| [Magick.NET](https://github.com/dlemstra/Magick.NET) / ImageMagick | Apache-2.0 / ImageMagick License | Image decoding, encoding and editing |
| [PDFsharp & MigraDoc 6.2](https://github.com/empira/PDFsharp) | MIT | PDF creation, merge, split, organize, document → PDF |
| [PDFtoImage](https://github.com/sungaila/PDFtoImage) / [PDFium](https://pdfium.googlesource.com/pdfium/) | MIT / BSD-3-Clause | PDF page rendering |
| [PdfPig](https://github.com/UglyToad/PdfPig) | Apache-2.0 | PDF text extraction |
| [Open XML SDK](https://github.com/dotnet/Open-XML-SDK) | MIT | DOCX reading |
| [HtmlToOpenXml](https://github.com/onizet/html2openxml) | MIT | DOCX writing |
| [Markdig](https://github.com/xoofx/markdig) | BSD-2-Clause | Markdown → HTML |
| [ReverseMarkdown](https://github.com/mysticmind/reversemarkdown-net) | MIT | HTML → Markdown |
| [AngleSharp](https://github.com/AngleSharp/AngleSharp) | MIT | HTML parsing |
| [RtfPipe](https://github.com/erdomke/RtfPipe) | MIT | RTF → HTML |
| [ClosedXML](https://github.com/ClosedXML/ClosedXML) | MIT | XLSX read/write |
| [SharpCompress](https://github.com/adamhathcock/sharpcompress) | MIT | RAR/7Z/BZ2/XZ extraction |
| [ZXing.Net](https://github.com/micjahn/ZXing.Net) | Apache-2.0 | QR code and barcode reading |
| [ONNX Runtime](https://github.com/microsoft/onnxruntime) | MIT | Running the background-removal model |
| [U²-Net (u2netp)](https://github.com/xuebinqin/U-2-Net) | Apache-2.0 | Background-removal model weights |
| [FFmpeg](https://ffmpeg.org) ([BtbN build](https://github.com/BtbN/FFmpeg-Builds)) | GPL-3.0 | Audio and video, shipped as separate `ffmpeg.exe`/`ffprobe.exe` in `tools\` (see `tools\licenses`) |

FFmpeg is not linked into Tangelo; it runs as a separate program. Its source is available from
https://ffmpeg.org/download.html.
