var dir = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "tangelo-selftest");
#if WINDOWS
Tangelo.Platform.Windows.WindowsServices.Register();
#endif
return await Tangelo.Core.SelfTest.Run(dir, Console.Out) == 0 ? 0 : 1;
