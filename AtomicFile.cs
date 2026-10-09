namespace RslCompanionUploader;

/// <summary>
/// Replaces a file so a reader sees the old contents or the new, never a half-written file: write a
/// sibling temp file, then move it over the target (a rename on one NTFS volume), retrying briefly while
/// a reader holds the target open.
///
/// <para>The same routine as the engine's <c>KnownOffsets.WriteAtomically</c>, kept here because the app
/// reaches the engine only through its native C API (<see cref="Engine"/>) and both write the local
/// offset catalog. Keep the two in step.</para>
/// </summary>
internal static class AtomicFile
{
    public static void Write(string path, string contents)
    {
        if (Path.GetDirectoryName(Path.GetFullPath(path)) is string dir) Directory.CreateDirectory(dir);
        string temp = $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temp, contents);
        try
        {
            for (int attempt = 1; ; attempt++)
            {
                try { File.Move(temp, path, overwrite: true); return; }
                catch (IOException) when (attempt < 5) { Thread.Sleep(50 * attempt); }
                catch (UnauthorizedAccessException) when (attempt < 5) { Thread.Sleep(50 * attempt); }
            }
        }
        finally
        {
            if (File.Exists(temp)) { try { File.Delete(temp); } catch { /* best effort */ } }
        }
    }
}
