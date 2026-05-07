using LibHac.Common;
using LibHac.Common.Keys;
using LibHac.Fs;
using LibHac.Fs.Fsa;
using LibHac.FsSystem;
using LibHac.NSZ;
using LibHac.Tools.Fs;
using LibHac.Tools.FsSystem;
using LibHac.Tools.FsSystem.NcaUtils;
using NSW.Avalonia.Services;
using NSW.Core;
using NSW.Core.Enums;
using NSW.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Path = System.IO.Path;
using Res = NSW.Core.Properties.Resources;

namespace NSW.M3.Avalonia.Services;

public static class NspCompressService
{
    public static async Task<List<string>> Compress(IReadOnlyList<string> inputPaths, int nczCompressionLevel, bool validation, bool useBlockMode, IProgress<ProgressInfo> progress, Action<string, LogLevel, string> log, CancellationToken ct = default)
        => await ExecuteProcess(inputPaths, true, nczCompressionLevel, validation, useBlockMode, progress, log, ct);

    public static async Task<List<string>> Decompress(IReadOnlyList<string> inputPaths, IProgress<ProgressInfo> progress, Action<string, LogLevel, string> log, CancellationToken ct = default) 
        => await ExecuteProcess(inputPaths, false, 0, false, false, progress, log, ct);

    private static async Task<List<string>> ExecuteProcess(IReadOnlyList<string> inputPaths, bool isCompressMode, int compressionLevel, bool validation, bool useBlockMode, IProgress<ProgressInfo> progress, Action<string, LogLevel, string> log, CancellationToken ct)
    {
        var ks = KeySetProvider.Instance.KeySet.Clone();
        var processedFiles = new List<string>();

        for (int i = 0; i < inputPaths.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            string path = inputPaths[i];

            try
            {
                string modeName = isCompressMode ? Res.Log_ModeCompress : Res.Log_ModeDecompress;

                log?.Invoke($"[{i + 1}/{inputPaths.Count}] {modeName} {Res.Log_ProcessStart}: {Path.GetFileName(path)}", LogLevel.Info, inputPaths[i]);

                string resultPath = await RunCoreAsync(path, isCompressMode, compressionLevel, validation, useBlockMode, ks, progress, log, ct);

                if (!string.IsNullOrEmpty(resultPath))
                    processedFiles.Add(resultPath);
            }
            catch (Exception ex)
            {
                log?.Invoke($"{Path.GetFileName(path)} {Res.Log_ProcessError}: {ex.Message}", LogLevel.Error, string.Empty);
            }
        }

        return processedFiles;
    }

    private static async Task<string> RunCoreAsync(string inputPath, bool isCompressMode, int compressionLevel, bool validation, bool useBlockMode, KeySet keySet, IProgress<ProgressInfo> progress, Action<string, LogLevel, string> log, CancellationToken ct)
    {
        var disposables = new List<IDisposable>();
        var converters = new Dictionary<string, NcaToNczConverter>(StringComparer.OrdinalIgnoreCase);
        string? finalPath = null;
        bool isCompleted = false;

        try
        {
            var metas = LibHacHelper.GetMetadataFromContainer(keySet, inputPath);

            if (metas.Count == 0) 
                throw new InvalidOperationException(Res.Error_NoMetadata);

            var meta = metas.First();
            var sourceStorage = new LocalStorage(inputPath, FileAccess.Read);
            disposables.Add(sourceStorage);
            IFileSystem sourceFs = sourceStorage.OpenFileSystem(keySet, inputPath);
            disposables.Add(sourceFs);
            keySet.RegisterTickets(sourceFs);

            string outputExt = isCompressMode ? ".nsz" : ".nsp";
            finalPath = Common.GetUniqueFilePath(Path.ChangeExtension(inputPath, outputExt));

            var fileEntries = new List<(string Name, Func<Stream, Action<long>, Task> Writer, long EstimatedSize, string Label)>();

            foreach (var entry in sourceFs.EnumerateEntries("/", "*"))
            {
                string entryName = entry.Name.ToString();
                string entryExt = Path.GetExtension(entryName).ToLowerInvariant();
                var fileRef = new UniqueRef<IFile>();

                if (!sourceFs.OpenFile(ref fileRef.Ref, entry.FullPath.ToU8Span(), OpenMode.Read).IsSuccess())
                    continue;

                fileRef.Get.GetSize(out long size).ThrowIfFailure();
                IFile rawFile = fileRef.Release();
                disposables.Add(rawFile);
                IStorage currentStorage = new FileStorage(rawFile);
                disposables.Add(currentStorage);

                if (entryExt is ".tik" or ".cert")
                    continue;

                if (entryExt is not ".nca" and not ".ncz")
                {
                    var capturedStorage = currentStorage;
                    fileEntries.Add((entryName, async (s, onRead) => await Common.CopyStreamAsync(capturedStorage.AsStream(), s, onRead, ct), size, entryName));

                    continue;
                }

                IStorage ncaStorage;
                string ncaName;
                long ncaSize;

                if (entryExt == ".ncz")
                {
                    var ncz = new Ncz(keySet, currentStorage.AsStream(), NczReadMode.Original);
                    ncz.BaseStorage.GetSize(out ncaSize).ThrowIfFailure();
                    ncaStorage = ncz.BaseStorage;
                    ncaName = Path.ChangeExtension(entryName, ".nca");
                }
                else
                {
                    ncaStorage = currentStorage;
                    ncaName = entryName;
                    ncaSize = size;
                }

                var nca = new Nca(keySet, ncaStorage);
                string label = $"{meta.KrTitle ?? meta.EnTitle} [{nca.Header.ContentType}]";

                if (isCompressMode && nca.Header.ContentType is NcaContentType.Program or NcaContentType.PublicData)
                {
                    string compName = Path.ChangeExtension(ncaName, ".ncz");
                    var converter = new NcaToNczConverter(keySet);
                    converters[ncaName] = converter;
                    var capturedStorage = ncaStorage;

                    fileEntries.Add((compName, async (s, onRead) =>
                    {
                        var recryptedHeader = await NcaRecryptService.GetRecryptedHeaderAsync(capturedStorage, 0, keySet, ct);
                        using var headerStream = new MemoryStream(recryptedHeader);
                        await converter.ConvertAsync(headerStream, capturedStorage, s, useBlockMode, compressionLevel, onRead, ct);
                    }, size, label));
                }
                else
                {
                    var capturedStorage = ncaStorage;
                    string statusLabel = isCompressMode ? Res.Log_StatusCopying : Res.Log_StatusDecompressing;
                    fileEntries.Add((ncaName, async (s, onRead) =>
                    {
                        await NcaRecryptService.RecryptAsync(capturedStorage.AsStream(), s, 0, keySet, onRead, ct);
                    }, ncaSize, $"{label} [{statusLabel}]"));
                }
            }

            string displayName = $"{(isCompressMode ? Res.Log_StatusCompressing : Res.Log_StatusDecompressing)} {NspNameBuilder.CompressDisplayNameBuild(meta.KrTitle, meta.TitleId, meta.DisplayVersion)}";
            var fout = File.Open(finalPath, FileMode.Create, FileAccess.ReadWrite);
            disposables.Add(fout);

            await Pfs0Builder.WriteAsync(displayName, Path.GetFileNameWithoutExtension(finalPath), fileEntries, fout, progress, ct);

            if (isCompressMode)
            {
                long originalSize = new FileInfo(inputPath).Length;
                long compressedSize = fout.Length;
                log?.Invoke($"{Res.Log_CompressionRatio}: {Common.FormatFileSize(originalSize)} → {Common.FormatFileSize(compressedSize)} ({compressedSize * 100.0 / originalSize:F1}%)", LogLevel.Highlight, meta.TitleId);

                if (validation)
                {
                    fout.Position = 0;
                    var validationPartitionFileSystem = new PartitionFileSystem();
                    validationPartitionFileSystem.Initialize(fout.AsStorage()).ThrowIfFailure();
                    var nczEntries = validationPartitionFileSystem.EnumerateEntries("/", "*.ncz")
                        .Where(e => converters.ContainsKey(Path.ChangeExtension(e.Name, ".nca")))
                        .ToList();
                    long totalValidationSize = nczEntries.Sum(e => fileEntries.FirstOrDefault(f => string.Equals(f.Name, e.Name, StringComparison.OrdinalIgnoreCase)).EstimatedSize);

                    foreach (var entry in nczEntries)
                    {
                        ct.ThrowIfCancellationRequested();
                        string origName = Path.ChangeExtension(entry.Name, ".nca");

                        if (!converters.TryGetValue(origName, out var converter)) 
                            continue;

                        using var nczFile = new UniqueRef<IFile>();
                        validationPartitionFileSystem.OpenFile(ref nczFile.Ref, entry.FullPath.ToU8Span(), OpenMode.Read).ThrowIfFailure();
                        string label = $"{meta.KrTitle ?? meta.EnTitle} [{meta.TitleId}]";

                        log?.Invoke($"- {label} {Res.Log_StatusValidating}", LogLevel.Info, meta.TitleId);

                        await converter.ValidateAsync(nczFile.Get.AsStream(), Path.GetFileNameWithoutExtension(finalPath), totalValidationSize, label, progress, ct);

                        log?.Invoke($"- {label} {Res.Log_ValidationComplete}", LogLevel.Ok, meta.TitleId);
                    }
                }
            }

            isCompleted = true;
            string modeDone = isCompressMode ? Res.Log_ModeCompress : Res.Log_ModeDecompress;

            log?.Invoke($"{Path.GetFileName(finalPath)} {modeDone} {Res.Log_StatusDone}", LogLevel.Ok, meta.TitleId);

            return finalPath;
        }
        finally
        {
            for (int i = disposables.Count - 1; i >= 0; i--) disposables[i]?.Dispose();

            if (!isCompleted && !string.IsNullOrEmpty(finalPath) && File.Exists(finalPath))
                try
                { 
                    File.Delete(finalPath); 
                } 
                catch { }
        }
    }
}