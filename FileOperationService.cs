using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace FileExplorerCS;

public class FileOperationResult
{
    public List<(string Source, string Dest)> SucceededMoves { get; } = new();
    public List<string> SucceededPaths { get; } = new();
    public List<(string Path, Exception Exception)> FailedPaths { get; } = new();
    public bool Success => FailedPaths.Count == 0;
}

public interface IFileOperationService
{
    Task<FileOperationResult> RecycleItemsAsync(List<string> paths, List<bool> isFolder, IProgress<string> progress);
    Task<FileOperationResult> CopyItemsAsync(List<string> sourcePaths, string targetDir, IProgress<string> progress);
    Task<FileOperationResult> MoveItemsAsync(List<string> sourcePaths, string targetDir, IProgress<string> progress);
}

public class FileOperationService : IFileOperationService
{
    public async Task<FileOperationResult> RecycleItemsAsync(List<string> paths, List<bool> isFolder, IProgress<string> progress)
    {
        var result = new FileOperationResult();
        await Task.Run(() =>
        {
            for (int i = 0; i < paths.Count; i++)
            {
                var path = paths[i];
                var isDir = isFolder[i];
                progress.Report($"Moving item {i + 1} of {paths.Count} to the Recycle Bin...");

                try
                {
                    if (isDir)
                    {
                        Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(
                            path,
                            Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                            Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin,
                            Microsoft.VisualBasic.FileIO.UICancelOption.ThrowException
                        );
                    }
                    else
                    {
                        Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                            path,
                            Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                            Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin,
                            Microsoft.VisualBasic.FileIO.UICancelOption.ThrowException
                        );
                    }
                    result.SucceededPaths.Add(path);
                }
                catch (Exception ex)
                {
                    result.FailedPaths.Add((path, ex));
                }
            }
        });
        return result;
    }

    public async Task<FileOperationResult> CopyItemsAsync(List<string> sourcePaths, string targetDir, IProgress<string> progress)
    {
        var result = new FileOperationResult();
        await Task.Run(() =>
        {
            for (int i = 0; i < sourcePaths.Count; i++)
            {
                var src = sourcePaths[i];
                var name = Path.GetFileName(src);
                var dest = Path.Combine(targetDir, name);

                progress.Report($"Copying item {i + 1} of {sourcePaths.Count} ({name})...");

                try
                {
                    if (Directory.Exists(src))
                    {
                        if (Directory.Exists(dest) || File.Exists(dest))
                        {
                            dest = GetUniqueFilePath(targetDir, name);
                        }
                        CopyDirectoryRecursively(src, dest);
                    }
                    else
                    {
                        if (File.Exists(dest) || Directory.Exists(dest))
                        {
                            dest = GetUniqueFilePath(targetDir, name);
                        }
                        File.Copy(src, dest);
                    }
                    result.SucceededPaths.Add(dest);
                }
                catch (Exception ex)
                {
                    result.FailedPaths.Add((src, ex));
                }
            }
        });
        return result;
    }

    private static void CopyDirectoryRecursively(string sourceDir, string destinationDir)
    {
        Directory.CreateDirectory(destinationDir);
        foreach (string file in Directory.GetFiles(sourceDir))
        {
            string destFile = Path.Combine(destinationDir, Path.GetFileName(file));
            File.Copy(file, destFile);
        }
        foreach (string subDir in Directory.GetDirectories(sourceDir))
        {
            string destSubDir = Path.Combine(destinationDir, Path.GetFileName(subDir));
            CopyDirectoryRecursively(subDir, destSubDir);
        }
    }

    public async Task<FileOperationResult> MoveItemsAsync(List<string> sourcePaths, string targetDir, IProgress<string> progress)
    {
        var result = new FileOperationResult();
        await Task.Run(() =>
        {
            for (int i = 0; i < sourcePaths.Count; i++)
            {
                var src = sourcePaths[i];
                var name = Path.GetFileName(src);
                var dest = Path.Combine(targetDir, name);

                progress.Report($"Moving item {i + 1} of {sourcePaths.Count} ({name})...");

                try
                {
                    if (File.Exists(dest) || Directory.Exists(dest))
                    {
                        dest = GetUniqueFilePath(targetDir, name);
                    }

                    if (Directory.Exists(src))
                    {
                        Directory.Move(src, dest);
                    }
                    else
                    {
                        File.Move(src, dest);
                    }
                    result.SucceededMoves.Add((src, dest));
                }
                catch (Exception ex)
                {
                    result.FailedPaths.Add((src, ex));
                }
            }
        });
        return result;
    }

    private static string GetUniqueFilePath(string directory, string fileName)
    {
        string baseName = Path.GetFileNameWithoutExtension(fileName);
        string extension = Path.GetExtension(fileName);
        string path = Path.Combine(directory, fileName);
        int counter = 1;

        while (File.Exists(path) || Directory.Exists(path))
        {
            path = Path.Combine(directory, $"{baseName} ({counter}){extension}");
            counter++;
        }

        return path;
    }
}
