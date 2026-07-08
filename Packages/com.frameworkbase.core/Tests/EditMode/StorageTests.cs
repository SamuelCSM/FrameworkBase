using System.Collections;
using System.IO;
using Cysharp.Threading.Tasks;
using Framework.Storage;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Framework.Tests
{
    public class StorageTests
    {
        private string _dir;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Application.temporaryCachePath, "StorageTests_" + Path.GetRandomFileName());
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, recursive: true);
        }

        [Test]
        public void AtomicWriteText_ReplacesFileAndCreatesBackup()
        {
            string path = Path.Combine(_dir, "data.txt");
            string backup = path + ".bak";
            IFileStorage storage = new LocalFileStorage();

            storage.WriteText(path, "old");
            storage.AtomicWriteText(path, "new", backup);

            Assert.AreEqual("new", storage.ReadText(path));
            Assert.AreEqual("old", storage.ReadText(backup));
        }

        [Test]
        public void TryDeleteFile_SuppressesMissingFile()
        {
            IFileStorage storage = new LocalFileStorage();

            Assert.IsTrue(storage.TryDeleteFile(Path.Combine(_dir, "missing.txt")));
        }

        [UnityTest]
        public IEnumerator ReadBytesAsync_ReadsOnThreadPool() => UniTask.ToCoroutine(async () =>
        {
            string path = Path.Combine(_dir, "bytes.bin");
            IFileStorage storage = new LocalFileStorage();
            storage.WriteBytes(path, new byte[] { 1, 2, 3 });

            byte[] bytes = await storage.ReadBytesAsync(path);

            CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, bytes);
        });

        [Test]
        public void AppendBytes_AppendsToExistingFile()
        {
            string path = Path.Combine(_dir, "bytes.bin");
            IFileStorage storage = new LocalFileStorage();

            storage.WriteBytes(path, new byte[] { 1, 2 });
            storage.AppendBytes(path, new byte[] { 3, 4 });

            CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 }, storage.ReadBytes(path));
        }
    }
}
