using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.CompilerServices;
using Silk.NET.Core.Native;

namespace Phalanx;
public static class FileSystem
{
    public const string EXTENSION_WORLD = ".world";
    public const string EXTENSION_MATERIAL = ".xml";
    public const string EXTENSION_MESH = ".mesh";
    public const string EXTENSION_CSHARP = ".cs";
    public const string EXTENSION_PREFAB = ".prefab";
    public const string EXTENSION_SHADER = ".shader";
    public const string EXTENSION_FONT = ".font";
    public const string EXTENSION_AUDIO = ".audio";
    public const string EXTENSION_TEXTURE = ".texture";

    // directories & files
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string GetFileNameFromFilePath(string path) { return Path.GetFileName(path) ?? string.Empty; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string GetFileNameWithoutExtensionFromFilePath(string path) { return Path.GetFileNameWithoutExtension(path) ?? string.Empty; }
    public static string GetDirectoryFromFilePath(string path)
    {
        string? directory = Path.GetDirectoryName(path);

        if (string.IsNullOrEmpty(directory))
            return string.Empty;

        char separator = Path.DirectorySeparatorChar;
        return directory.EndsWith(separator) || directory.EndsWith(Path.AltDirectorySeparatorChar) ? directory : directory + separator;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string GetFilePathWithoutExtension(string path) { return GetDirectoryFromFilePath(path) + GetFileNameWithoutExtensionFromFilePath(path); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string ReplaceExtension(string path, string extension) { return GetFilePathWithoutExtension(path) + extension; }
    public static string GetExtensionFromFilePath(string path)
    {
        // A system_error is possible if the characters are
        // something that can't be converted, like Russian.
        string extension = Path.GetExtension(path);
        if (extension is not null)
            return extension;

        Log.LogWarning($"Failed to get extension from path '{path}'.");
        return string.Empty;
    }
    public static string GetRelativePath(string path)
    {
        if (!Path.IsPathRooted(path))
            return path;

        string workingDirectory = GetWorkingDirectory();

        return Path.GetRelativePath(workingDirectory, path);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string GetWorkingDirectory() { return Directory.GetCurrentDirectory(); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string GetRootDirectory(string path) { return Path.GetPathRoot(path) ?? string.Empty; }
    public static string GetParentDirectory(string path)
    {
        string? parent = Path.GetDirectoryName(path);

        // If there is not parent path, return path as is
        if (string.IsNullOrEmpty(parent))
            return path;

        return parent;
    }
    public static List<string> GetDirectoriesInDirectory(string path)
    {
        var directories = new List<string>();

        if (!IsDirectory(path))
            return directories;
        try
        {
            directories.AddRange(Directory.GetDirectories(path));
            return directories;
        }
        catch (Exception ex)
        {
            Log.LogWarning($"Failed to read directories in '{path}'. {ex.Message}");
            return directories;
        }
    }
    public static List<string> GetFilesInDirectory(string path)
    {
        var files = new List<string>();

        if (!IsDirectory(path))
            return files;

        try
        {
            files.AddRange(Directory.GetFiles(path));
/*            var dirInfo = new DirectoryInfo(path);
              foreach (FileInfo file in dirInfo.EnumerateFiles())
                  files.Add(file.FullName);
*/
        }
        catch (Exception e)
        {
            Log.LogWarning($"Failed to read files in directory '{path}'. {e.Message}");
        }

        return files;
    }
    public static List<string> SplitPath(string path)
    {
        string root = GetRootDirectory(path);
        string rest = path.Substring(root.Length);

        var components = new List<string>();

        if (!string.IsNullOrEmpty(root))
            components.Add(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        if (!string.IsNullOrEmpty(rest))
        {
            components.AddRange(
                                rest.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                                                   StringSplitOptions.RemoveEmptyEntries)
                                );
        }

        return components;
    }
    public static string GetLastWriteTime(string path)
    {
        try
        {
            DateTime lastWrite = File.GetLastWriteTime(path);
            return lastWrite.ToString("yyyy-MM-dd HH:mm:ss");
        }
        catch (Exception ex)
        {
            Log.LogError($"Failed to get last write time for {path}: {ex.Message}");
            return "Unknown";
        }
    }
    public static void Rename(string oldName, string newName)
    {
        try
        {
            if (IsFile(oldName))
            {
                File.Move(oldName, newName, true);
            }
            else if (IsDirectory(oldName))
            {
                if (IsDirectory(newName))
                    Directory.Delete(newName, true);
                Directory.Move(oldName, newName);
            }
            else
            {
                Log.LogError($"Failed to rename '{oldName}' to '{newName}': Source path does not exist");
            }
        }
        catch (Exception ex)
        {
            Log.LogError($"Failed to rename '{oldName}' to '{newName}': {ex.Message}");
        }
    }

    public static bool Exists(string path)
    {
        if (IsFile(path))
            return true;
        else if (IsDirectory(path))
            return true;
        else
            Log.LogError("Path doesn't not exist");
        return false;
    }

    public static bool IsDirectoryEmpty(string path)
    {
        if (!IsDirectory(path))
        {
            Log.LogError("Directory doesn't not exist");
            return false;
        }

        if (Directory.EnumerateFileSystemEntries(path).Any())
            return false;

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsDirectory(string path){ return Directory.Exists(path); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsFile(string path) { return File.Exists(path); }

    public static bool Delete(string path)
    {
        if (IsFile(path))
        {
            try
            {
                File.Delete(path);
                return true;
            }
            catch (Exception ex)
            {
                Log.LogError($"Failed to delete '{path}': {ex.Message}");
            }
        }
        else if (IsDirectory(path))
        {
            try
            {
                Directory.Delete(path, true);
                return true;
            }
            catch (Exception ex)
            {
                Log.LogError($"Failed to delete '{path}': {ex.Message}");
            }
        }
        else
        {
            Log.LogError($"Failed to delete '{path}': Source path does not exist");
        }
        return false;
    }
    public static bool CreateDirectory(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            return true;
        }
        catch (Exception ex)
        {
            Log.LogError($"Failed to create '{path}': {ex.Message}");
        }
        return false;
    }
    public static bool CopyFileFromTo(string source, string destination)
    {
        if (source == destination)
            return true;

        // In case the destination path doesn't exist, create it
        if (!Exists(GetDirectoryFromFilePath(destination)))
            CreateDirectory(GetDirectoryFromFilePath(destination));

        try
        {
            File.Copy(source, destination);
            return true;
        }
        catch (Exception ex)
        {
            Log.LogError($"Failed to copy '{source}' to '{destination}': {ex.Message}");
        }
        return false;
    }
    public static bool WriteFile(string path, byte[] data)
    {
        if (string.IsNullOrEmpty(path))
            return false;
        try
        {
            File.WriteAllBytes(path, data);
            return true;
        }
        catch (Exception ex)
        {
            Log.LogError($"Failed to write '{path}' data: {ex.Message}");
            return false;
        }
    }
    public static bool ReadFile(string path, out byte[] data)
    {
        data = Array.Empty<byte>();

        if (string.IsNullOrEmpty(path))
            return false;
        try
        {
            data = File.ReadAllBytes(path);
            return true;
        }
        catch (Exception ex)
        {
            Log.LogError($"Failed to read '{path}' data: {ex.Message}");
            data = Array.Empty<byte>();
            return false;
        }
    }

    // strings
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsEmptyOrWhitespace(string? str) { return string.IsNullOrWhiteSpace(str); }
}