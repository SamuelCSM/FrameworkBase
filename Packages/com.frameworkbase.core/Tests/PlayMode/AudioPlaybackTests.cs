using System.Collections;
using System.Threading;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Framework.Tests.PlayMode
{
    public class AudioPlaybackTests
    {
        [UnityTest]
        public IEnumerator AudioSource自然结束_Lease在真实帧循环归还() => UniTask.ToCoroutine(async () =>
        {
            var provider = new FakeProvider();
            var manager = new AudioManager(provider, new UnityAudioPlaybackProbe());
            manager.OnInit();

            try
            {
                AudioPlaybackHandle handle = await manager.PlaySoundAsync("short");
                float deadline = Time.realtimeSinceStartup + 2f;
                while (handle.IsValid && Time.realtimeSinceStartup < deadline)
                {
                    manager.OnUpdate(Time.unscaledDeltaTime);
                    await UniTask.Yield();
                }

                Assert.IsFalse(handle.IsValid, "短音效应在真实 AudioSource 播放结束后进入完成态");
                Assert.AreEqual(0, provider.ActiveReferences, "自然结束必须归还 AudioClip Lease");
            }
            finally
            {
                manager.OnShutdown();
                provider.Dispose();
            }
        });

        private sealed class FakeProvider : IAudioAssetLeaseProvider, System.IDisposable
        {
            private readonly AudioClip _clip = AudioClip.Create("short", 400, 1, 8000, false);
            public int ActiveReferences { get; private set; }

            public UniTask<AssetLease<AudioClip>> LoadAsync(string address, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ActiveReferences++;
                return UniTask.FromResult(new AssetLease<AudioClip>(address, _clip, _ => ActiveReferences--));
            }

            public void Dispose()
            {
                Object.Destroy(_clip);
            }
        }
    }
}
