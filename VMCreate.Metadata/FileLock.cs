using System.Security.Cryptography;
using System.Text;

namespace VMCreate.Metadata
{
    /// <summary>A named mutex per file, shared by every process in the session (VMCreate and the Manager).</summary>
    internal sealed class FileLock
    {
        private readonly string _mutexName;

        public FileLock(string kind, string fullPath)
        {
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(fullPath.ToUpperInvariant()));
            _mutexName = $@"Local\VMCreate.{kind}." + Convert.ToHexString(hash, 0, 8);
        }

        public T Run<T>(Func<T> action, CancellationToken cancellationToken)
        {
            using var mutex = new Mutex(false, _mutexName);
            bool acquired = false;
            try
            {
                while (!acquired)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        acquired = mutex.WaitOne(TimeSpan.FromMilliseconds(250));
                    }
                    catch (AbandonedMutexException)
                    {
                        // A writer died mid-update. Writers only make atomic or
                        // append-only changes, so the file is still consistent.
                        acquired = true;
                    }
                }
                return action();
            }
            finally
            {
                if (acquired)
                    mutex.ReleaseMutex();
            }
        }
    }
}
