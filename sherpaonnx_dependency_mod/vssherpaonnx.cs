using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using Vintagestory.API.Common;
using Vintagestory.API.Client;

namespace VSSherpaOnnx
{
	public sealed class VSSherpaOnnxSystem : ModSystem
	{
		private static readonly object		Sync = new();
		private static bool					HasInitialized;
		private static Assembly?			SherpaONNXAssembly;
		private static string?				AssemblyDirectory;
		private static string?				OSID;
		private static string?				NativeDirectory;
		private static ICoreAPI?			capi;

		public override bool ShouldLoad(EnumAppSide forSide) => true; // load on both; do work only on client
		public override void Start(ICoreAPI api) { capi = api; }

		public override void StartClientSide(ICoreClientAPI api) // Self initialization ASAP so as not to wait for the mods that depend on us
		{
			try { EnsureSherpaReady(api); }
			catch (Exception ex) { api.Logger.Error("[vssherpaonnx] Initialization failed: {0}", ex); throw; }
		}

		public static Assembly EnsureSherpaReady(ICoreAPI api)
		{
			if (api.Side != EnumAppSide.Client) { throw new InvalidOperationException("[vssherpaonnx] Back off server. EnsureSherpaReady must be called on the client side."); }

			lock (Sync)
			{
				if (HasInitialized && SherpaONNXAssembly != null) return SherpaONNXAssembly;

				capi = api;
				AssemblyDirectory = Path.GetDirectoryName(typeof(VSSherpaOnnxSystem).Assembly.Location) ?? throw new InvalidOperationException("[vssherpaonnx] Could not resolve assembly directory.");

				OSID = GetRidOrThrow();
				NativeDirectory = Path.Combine(AssemblyDirectory, "native", OSID);

				var onnxPath	= Path.Combine(NativeDirectory, LibName("onnxruntime"));
				var sherpaPath	= Path.Combine(NativeDirectory, LibName("sherpa-onnx-c-api"));
				var managed		= Path.Combine(AssemblyDirectory, "sherpa-onnx.dll");
				if (!File.Exists(managed)) throw new FileNotFoundException($"[vssherpaonnx] Missing managed wrapper: {managed}");
				if (!File.Exists(onnxPath)) throw new FileNotFoundException($"[vssherpaonnx] Missing native onnxruntime: {onnxPath}");
				if (!File.Exists(sherpaPath)) throw new FileNotFoundException($"[vssherpaonnx] Missing native sherpa-onnx-c-api: {sherpaPath}");

				// Load natives by absolute path (onnxruntime first, then sherpa C API)
				LoadNativeExact(onnxPath);
				LoadNativeExact(sherpaPath);

				// Load managed wrapper
				SherpaONNXAssembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(managed);

				// DllImport resolver so P/Invokes in sherpa-onnx.dll resolves to our packaged natives
				NativeLibrary.SetDllImportResolver(SherpaONNXAssembly, (name, assembly, searchPath) =>
				{
					try
					{
						string resolvedpath = Path.Combine(NativeDirectory!, LibName(name));
						if (File.Exists(resolvedpath)) return NativeLibrary.Load(resolvedpath);
					}
					catch { }
					return IntPtr.Zero;
				});

				HasInitialized = true;
				capi?.Logger.Notification($"[vssherpaonnx] Ready for {OSID} at {NativeDirectory}.");
				
				return SherpaONNXAssembly;
			}
		}

		public static string GetResolvedRid()
		{
			if (OSID == null) OSID = GetRidOrThrow();
			return OSID;
		}

		public static string GetResolvedNativeDirectory()
		{
			if (NativeDirectory != null) return NativeDirectory;
			var dir = Path.GetDirectoryName(typeof(VSSherpaOnnxSystem).Assembly.Location) ?? throw new InvalidOperationException("[vssherpaonnx] Could not resolve assembly directory.");
			NativeDirectory = Path.Combine(dir, "native", GetRidOrThrow());
			return NativeDirectory;
		}

		public static string GetManagedWrapperPath()
		{
			var dir = Path.GetDirectoryName(typeof(VSSherpaOnnxSystem).Assembly.Location) ?? throw new InvalidOperationException("[vssherpaonnx] Could not resolve assembly directory.");
			return Path.Combine(dir, "sherpa-onnx.dll");
		}

		private static string GetRidOrThrow()
		{
			var processorarchitecture = RuntimeInformation.ProcessArchitecture;


			if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return "win-x64";
			if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
			{	// "linuxarm" when the game adds support for it... and someone can compile it for us
				if (processorarchitecture != Architecture.X64) { throw new NotSupportedException($"[vssherpaonnx] Unsupported architecture: {processorarchitecture}. Only x64 is currently supported."); }
				else { return "linux-x64"; }
			}
			if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return "osx-x64"; // Our OSX-x64 is actually compatible with all current OSX architrcutres
			// Note: We don't check for ARM or 32bit windows/macos since the base game does not support it.

			throw new NotSupportedException($"[vssherpaonnx] Unknown or unsupported OS/Architecture, ({processorarchitecture}). Please message me what OS you're trying to use and I'll try to add support for it.");
		}

		private static string LibName(string baseName)
		{
			if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))	return baseName + ".dll";
			if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))		return "lib" + baseName + ".so";
																		return "lib" + baseName + ".dylib"; // MacOS
		}

		private static void LoadNativeExact(string fullPath)
		{
			try { NativeLibrary.Load(fullPath); }
			catch (Exception ex) { throw new DllNotFoundException($"[vssherpaonnx] Failed to load native: {fullPath}", ex); }
		}
	}
}
