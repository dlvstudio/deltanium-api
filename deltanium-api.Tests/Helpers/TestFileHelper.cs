using System;
using System.IO;

namespace deltanium_api.Tests.Helpers
{
    /// <summary>
    /// Helper for managing test files and temporary directories
    /// </summary>
    public class TestFileHelper : IDisposable
    {
        public string TestDirectory { get; }

        public TestFileHelper()
        {
            TestDirectory = Path.Combine(Path.GetTempPath(), "deltanium-api-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(TestDirectory);
        }

        public string GetFilePath(string fileName)
        {
            return Path.Combine(TestDirectory, fileName);
        }

        public void CreateFile(string fileName, string content)
        {
            var filePath = GetFilePath(fileName);
            File.WriteAllText(filePath, content);
        }

        public string ReadFile(string fileName)
        {
            var filePath = GetFilePath(fileName);
            return File.ReadAllText(filePath);
        }

        public bool FileExists(string fileName)
        {
            var filePath = GetFilePath(fileName);
            return File.Exists(filePath);
        }

        public void Dispose()
        {
            if (Directory.Exists(TestDirectory))
            {
                try
                {
                    Directory.Delete(TestDirectory, recursive: true);
                }
                catch
                {
                    // Ignore cleanup errors
                }
            }
        }
    }
}









