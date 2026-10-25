using System;
using System.IO;
using System.Threading;

namespace PhigrosEnhancer
{
    // Writes a file atomically: the caller produces a sibling temp file, then the helper
    // commits it with File.Replace (target exists) or File.Move (new target).
    //
    // On Windows, antivirus real-time scanning, search indexing and compressed-volume
    // workers can briefly hold a freshly written or recently touched file without sharing
    // delete. That makes ReplaceFile fail with "cannot delete the file to be replaced"
    // (Win32 1175) or a plain sharing violation (32). Those transient codes are retried a
    // bounded number of times with a short delay; every other failure is rethrown
    // unchanged so real errors (permissions, missing files, full disk) surface immediately.
    internal static class AtomicFile
    {
        const int MaxAttempts = 5;
        const int RetryDelayMilliseconds = 25;

        public static void Write(string path, Action<string> writeTemp)
        {
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                writeTemp(temp);
                Commit(temp, path);
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
        }
        static void Commit(string temp, string path)
        {
            for (int attempt = 1; ; attempt++)
            {
                // Re-check on every attempt: the target can appear between attempts, in
                // which case the next attempt naturally switches from Move to Replace.
                bool moveBranch = !File.Exists(path);
                try
                {
                    if (moveBranch) File.Move(temp, path);
                    else File.Replace(temp, path, null);
                    return;
                }
                catch (IOException ex)
                {
                    if (attempt >= MaxAttempts || !IsRetryable(ex, moveBranch)) throw;
                    Thread.Sleep(RetryDelayMilliseconds);
                }
            }
        }
        static bool IsRetryable(IOException ex, bool moveBranch)
        {
            int code = ex.HResult & 0xFFFF;
            if (code == 32 || code == 33 || code == 1175 || code == 1176 || code == 1177) return true;
            // ERROR_FILE_EXISTS (80) / ERROR_ALREADY_EXISTS (183) can only mean the target
            // appeared between the existence check and File.Move, so the next attempt must
            // re-check and commit through File.Replace instead of deleting the target.
            return moveBranch && (code == 80 || code == 183);
        }
    }
}
