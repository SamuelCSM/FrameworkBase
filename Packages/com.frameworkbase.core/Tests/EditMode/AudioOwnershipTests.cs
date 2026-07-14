using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Framework.Tests
{
    public class AudioOwnershipTests
    {
        private FakeLeaseProvider _provider;
        private FakePlaybackProbe _probe;
        private AudioManager _manager;

        [SetUp]
        public void SetUp()
        {
            _provider = new FakeLeaseProvider();
            _probe = new FakePlaybackProbe();
            _manager = new AudioManager(_provider, _probe);
            _manager.OnInit();
        }

        [TearDown]
        public void TearDown()
        {
            _manager?.OnShutdown();
            _provider.Dispose();
        }

        private static T Wait<T>(UniTask<T> task) => task.GetAwaiter().GetResult();
        private static void Wait(UniTask task) => task.GetAwaiter().GetResult();

        [Test]
        public void 自然结束_归还Lease且句柄失效()
        {
            AudioPlaybackHandle handle = Wait(_manager.PlaySoundAsync("click"));
            Assert.AreEqual(1, _provider.ActiveReferences);
            Assert.IsTrue(handle.IsValid);

            _probe.IsPlaying = false;
            _manager.OnUpdate(0.016f);

            Assert.AreEqual(0, _provider.ActiveReferences);
            Assert.AreEqual(0, _manager.ActiveSoundCount);
            Assert.IsFalse(handle.IsValid);
            Assert.AreEqual(AudioPlaybackState.Completed, handle.State);
        }

        [Test]
        public void 主动停止_重复终态只释放一次()
        {
            AudioPlaybackHandle handle = Wait(_manager.PlaySoundAsync("click"));

            Assert.IsTrue(handle.Stop());
            Assert.IsFalse(handle.Stop());
            _manager.OnUpdate(0.016f);

            Assert.AreEqual(1, _provider.ReleaseCount);
            Assert.AreEqual(0, _provider.ActiveReferences);
        }

        [Test]
        public void StopAll_释放全部活动播放()
        {
            Wait(_manager.PlaySoundAsync("a"));
            Wait(_manager.PlaySoundAsync("b"));
            Wait(_manager.PlaySoundAsync("a"));

            _manager.StopAllSounds();

            Assert.AreEqual(0, _manager.ActiveSoundCount);
            Assert.AreEqual(0, _provider.ActiveReferences);
            Assert.AreEqual(3, _provider.ReleaseCount);
        }

        [Test]
        public void Shutdown_释放活动播放且幂等()
        {
            Wait(_manager.PlaySoundAsync("a"));
            Wait(_manager.PlaySoundAsync("b"));

            _manager.OnShutdown();
            _manager.OnShutdown();

            Assert.AreEqual(0, _provider.ActiveReferences);
            Assert.AreEqual(2, _provider.ReleaseCount);
        }

        [Test]
        public void Shutdown_取消加载中播放且迟到资源归还()
        {
            _provider.HoldLoads = true;
            UniTask<AudioPlaybackHandle> pending = _manager.PlaySoundAsync("slow");

            _manager.OnShutdown();
            Assert.Throws<OperationCanceledException>(() => Wait(pending));
            Assert.AreEqual(1, _provider.ActiveReferences);

            _provider.ReleasePending();

            Assert.AreEqual(0, _provider.ActiveReferences);
            Assert.AreEqual(0, _manager.PendingSoundCount);
        }

        [Test]
        public void 背景音乐切换与Shutdown_每份Lease恰好释放一次()
        {
            Wait(_manager.PlayMusicAsync("music/a"));
            Assert.AreEqual(1, _provider.ActiveReferences);

            Wait(_manager.PlayMusicAsync("music/b"));
            Assert.AreEqual(1, _provider.ActiveReferences, "切换时先释放旧音乐再持有新音乐");
            Assert.AreEqual(1, _provider.ReleaseCount);

            _manager.OnShutdown();
            Assert.AreEqual(0, _provider.ActiveReferences);
            Assert.AreEqual(2, _provider.ReleaseCount);
        }

        [Test]
        public void 加载失败_不产生播放与残留引用()
        {
            _provider.FailLoads = true;
            LogAssert.Expect(LogType.Error, new Regex("PlaySoundAsync: 加载音效失败 - missing"));

            AudioPlaybackHandle handle = Wait(_manager.PlaySoundAsync("missing"));

            Assert.IsFalse(handle.IsValid);
            Assert.AreEqual(0, _manager.ActiveSoundCount);
            Assert.AreEqual(0, _provider.ActiveReferences);
        }

        [Test]
        public void 加载中取消_调用立即结束且迟到引用自动归还()
        {
            _provider.HoldLoads = true;
            var cts = new CancellationTokenSource();
            UniTask<AudioPlaybackHandle> pending = _manager.PlaySoundAsync("slow", cancellationToken: cts.Token);

            cts.Cancel();
            Assert.Throws<OperationCanceledException>(() => Wait(pending));
            Assert.AreEqual(1, _provider.ActiveReferences, "共享底层加载完成前不能提前释放");

            _provider.ReleasePending();

            Assert.AreEqual(0, _provider.ActiveReferences);
            Assert.AreEqual(0, _manager.PendingSoundCount);
            cts.Dispose();
        }

        [Test]
        public void Pause时isPlaying为false_不得误判自然结束()
        {
            AudioPlaybackHandle handle = Wait(_manager.PlaySoundAsync("loop"));
            Assert.IsTrue(handle.Pause());
            _probe.IsPlaying = false;

            _manager.OnUpdate(0.016f);

            Assert.IsTrue(handle.IsValid);
            Assert.AreEqual(AudioPlaybackState.Paused, handle.State);
            Assert.AreEqual(1, _provider.ActiveReferences);
            Assert.IsTrue(handle.Resume());
        }

        [Test]
        public void 同音效重复播放一千次_引用计数回到基线()
        {
            for (int i = 0; i < 1000; i++)
            {
                AudioPlaybackHandle handle = Wait(_manager.PlaySoundAsync("click"));
                Assert.IsTrue(handle.Stop());
            }

            Assert.AreEqual(1000, _provider.AcquireCount);
            Assert.AreEqual(1000, _provider.ReleaseCount);
            Assert.AreEqual(0, _provider.ActiveReferences);
            Assert.AreEqual(0, _manager.ActiveSoundCount);
        }

        [Test]
        public void 旧句柄不能停止AudioSource复用后的新播放()
        {
            AudioPlaybackHandle first = Wait(_manager.PlaySoundAsync("a"));
            first.Stop();
            AudioPlaybackHandle second = Wait(_manager.PlaySoundAsync("b"));

            Assert.IsFalse(first.Stop());
            Assert.IsTrue(second.IsValid);
            Assert.AreEqual(1, _provider.ActiveReferences);
        }

        private sealed class FakePlaybackProbe : IAudioPlaybackProbe
        {
            public bool IsPlaying = true;
            bool IAudioPlaybackProbe.IsPlaying(AudioSource source) => IsPlaying;
        }

        private sealed class FakeLeaseProvider : IAudioAssetLeaseProvider, IDisposable
        {
            private readonly AudioClip _clip = AudioClip.Create("test", 64, 1, 8000, false);
            private readonly List<UniTaskCompletionSource<AudioClip>> _pending = new List<UniTaskCompletionSource<AudioClip>>();

            public int AcquireCount;
            public int ReleaseCount;
            public int ActiveReferences;
            public bool FailLoads;
            public bool HoldLoads;

            public UniTask<AssetLease<AudioClip>> LoadAsync(string address, CancellationToken cancellationToken)
            {
                AcquireCount++;
                ActiveReferences++;

                UniTask<AudioClip> load;
                if (HoldLoads)
                {
                    var gate = new UniTaskCompletionSource<AudioClip>();
                    _pending.Add(gate);
                    load = gate.Task;
                }
                else if (FailLoads)
                {
                    ActiveReferences--; // 模拟 ResourceManager 失败回滚。
                    load = UniTask.FromResult<AudioClip>(null);
                }
                else
                {
                    load = UniTask.FromResult(_clip);
                }

                return AssetLeaseCoordinator.AcquireStartedAsync(
                    address,
                    load,
                    _ =>
                    {
                        ActiveReferences--;
                        ReleaseCount++;
                    },
                    cancellationToken);
            }

            public void ReleasePending()
            {
                for (int i = 0; i < _pending.Count; i++)
                    _pending[i].TrySetResult(_clip);
                _pending.Clear();
            }

            public void Dispose()
            {
                ReleasePending();
                UnityEngine.Object.DestroyImmediate(_clip);
            }
        }
    }
}
