using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace Framework
{
    internal interface IAudioAssetLeaseProvider
    {
        UniTask<AssetLease<AudioClip>> LoadAsync(string address, CancellationToken cancellationToken);
    }

    internal sealed class ResourceAudioAssetLeaseProvider : IAudioAssetLeaseProvider
    {
        public UniTask<AssetLease<AudioClip>> LoadAsync(string address, CancellationToken cancellationToken)
        {
            ResourceManager resources = Core.GameEntry.Resource;
            if (resources == null)
                throw new InvalidOperationException("ResourceManager 尚未初始化。");
            return resources.LoadLeaseAsync<AudioClip>(address, cancellationToken);
        }
    }

    internal interface IAudioPlaybackProbe
    {
        bool IsPlaying(AudioSource source);
    }

    internal sealed class UnityAudioPlaybackProbe : IAudioPlaybackProbe
    {
        public bool IsPlaying(AudioSource source) => source != null && source.isPlaying;
    }

    /// <summary>
    /// 音频管理器。所有 AudioClip 均由 AssetLease 显式持有；自然结束、主动停止、StopAll、
    /// Shutdown、加载取消和失败全部汇入同一个幂等完成路径。
    /// </summary>
    public class AudioManager : Core.FrameworkComponent<AudioManager>
    {
        private sealed class PlaybackRecord
        {
            public long Id;
            public AudioSource Source;
            public AssetLease<AudioClip> Lease;
            public CancellationTokenSource Lifetime;
            public AudioPlaybackState State;
        }

        private enum CompletionReason
        {
            Natural,
            Stopped,
            StopAll,
            Shutdown
        }

        private readonly IAudioAssetLeaseProvider _assetProvider;
        private readonly IAudioPlaybackProbe _playbackProbe;
        private readonly Dictionary<long, PlaybackRecord> _activeSounds = new Dictionary<long, PlaybackRecord>();
        private readonly Dictionary<long, CancellationTokenSource> _pendingSounds = new Dictionary<long, CancellationTokenSource>();
        private readonly List<long> _completionBuffer = new List<long>();

        private AudioSourcePool _audioSourcePool;
        private AudioSource _musicSource;
        private AssetLease<AudioClip> _musicLease;
        private CancellationTokenSource _musicOperationCts;
        private CancellationTokenSource _lifetimeCts;
        private string _currentMusicAddress;
        private long _nextPlaybackId;
        private bool _isShuttingDown;

        private float _masterVolume = 1f;
        private float _musicVolume = 1f;
        private float _soundVolume = 1f;
        private bool _isMuted;

        public AudioManager() : this(new ResourceAudioAssetLeaseProvider(), new UnityAudioPlaybackProbe()) { }

        internal AudioManager(IAudioAssetLeaseProvider assetProvider, IAudioPlaybackProbe playbackProbe)
        {
            _assetProvider = assetProvider ?? throw new ArgumentNullException(nameof(assetProvider));
            _playbackProbe = playbackProbe ?? throw new ArgumentNullException(nameof(playbackProbe));
        }

        public float MasterVolume
        {
            get => _masterVolume;
            set { _masterVolume = Mathf.Clamp01(value); UpdateAllVolumes(); }
        }

        public float MusicVolume
        {
            get => _musicVolume;
            set { _musicVolume = Mathf.Clamp01(value); UpdateMusicVolume(); }
        }

        public float SoundVolume
        {
            get => _soundVolume;
            set { _soundVolume = Mathf.Clamp01(value); UpdateSoundVolumes(); }
        }

        public bool IsMuted
        {
            get => _isMuted;
            set { _isMuted = value; UpdateAllVolumes(); }
        }

        internal int ActiveSoundCount => _activeSounds.Count;
        internal int PendingSoundCount => _pendingSounds.Count;

        public override void OnInit()
        {
            _isShuttingDown = false;
            _lifetimeCts?.Dispose();
            _lifetimeCts = new CancellationTokenSource();
            _audioSourcePool = new AudioSourcePool(10);
            CreateMusicSource();
            GameLog.Log("AudioManager 初始化");
        }

        public override void OnUpdate(float deltaTime)
        {
            _completionBuffer.Clear();
            foreach (KeyValuePair<long, PlaybackRecord> pair in _activeSounds)
            {
                PlaybackRecord record = pair.Value;
                if (record.State == AudioPlaybackState.Playing &&
                    (record.Source == null || !_playbackProbe.IsPlaying(record.Source)))
                {
                    _completionBuffer.Add(pair.Key);
                }
            }

            for (int i = 0; i < _completionBuffer.Count; i++)
                CompleteSound(_completionBuffer[i], CompletionReason.Natural);
        }

        public override void OnShutdown()
        {
            if (_isShuttingDown)
                return;

            _isShuttingDown = true;
            CancelCurrentMusicOperation();
            try { _lifetimeCts?.Cancel(); } catch { }

            StopMusicImmediate();
            StopAllSounds(CompletionReason.Shutdown);
            _audioSourcePool?.Clear();
            _audioSourcePool = null;

            if (_musicSource != null)
            {
                DestroyGameObject(_musicSource.gameObject);
                _musicSource = null;
            }

            _lifetimeCts?.Dispose();
            _lifetimeCts = null;
            GameLog.Log("AudioManager 关闭");
        }

        /// <summary>播放背景音乐。新调用会取消上一条未完成的音乐加载/淡变操作。</summary>
        public async UniTask PlayMusicAsync(
            string address,
            bool loop = true,
            float fadeTime = 0f,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(address))
                throw new ArgumentException("音乐地址不能为空。", nameof(address));
            ThrowIfUnavailable();

            if (_currentMusicAddress == address && _musicSource != null && _musicSource.isPlaying)
                return;

            CancellationTokenSource operation = BeginMusicOperation(cancellationToken);
            AssetLease<AudioClip> incomingLease = null;
            try
            {
                await StopMusicCoreAsync(fadeTime, operation.Token);
                incomingLease = await _assetProvider.LoadAsync(address, operation.Token);
                if (incomingLease == null)
                {
                    GameLog.Error($"PlayMusicAsync: 加载音乐失败 - {address}");
                    return;
                }

                operation.Token.ThrowIfCancellationRequested();
                _musicLease = incomingLease;
                incomingLease = null; // 所有权转交给 Manager。
                _currentMusicAddress = address;
                _musicSource.clip = _musicLease.Asset;
                _musicSource.loop = loop;

                if (fadeTime > 0f)
                {
                    _musicSource.volume = 0f;
                    _musicSource.Play();
                    await FadeVolumeAsync(_musicSource, GetEffectiveMusicVolume(), fadeTime, operation.Token);
                }
                else
                {
                    _musicSource.volume = GetEffectiveMusicVolume();
                    _musicSource.Play();
                }
            }
            finally
            {
                incomingLease?.Dispose();
                if (ReferenceEquals(_musicOperationCts, operation))
                    _musicOperationCts = null;
                operation.Dispose();
            }
        }

        public void StopMusic(float fadeTime = 0f)
        {
            if (_musicSource == null)
                return;

            CancellationTokenSource operation = BeginMusicOperation(CancellationToken.None);
            StopMusicOperationAsync(fadeTime, operation).Forget();
        }

        public void PauseMusic()
        {
            if (_musicSource != null && _musicSource.isPlaying)
                _musicSource.Pause();
        }

        public void ResumeMusic()
        {
            if (_musicSource != null && _musicSource.clip != null)
                _musicSource.UnPause();
        }

        private async UniTaskVoid StopMusicOperationAsync(float fadeTime, CancellationTokenSource operation)
        {
            try { await StopMusicCoreAsync(fadeTime, operation.Token); }
            catch (OperationCanceledException) { }
            finally
            {
                if (ReferenceEquals(_musicOperationCts, operation))
                    _musicOperationCts = null;
                operation.Dispose();
            }
        }

        private async UniTask StopMusicCoreAsync(float fadeTime, CancellationToken cancellationToken)
        {
            AssetLease<AudioClip> leaseToRelease = _musicLease;
            _musicLease = null;
            _currentMusicAddress = null;

            try
            {
                if (_musicSource != null && _musicSource.clip != null && fadeTime > 0f && _musicSource.isPlaying)
                    await FadeVolumeAsync(_musicSource, 0f, fadeTime, cancellationToken);
            }
            finally
            {
                if (_musicSource != null)
                {
                    _musicSource.Stop();
                    _musicSource.clip = null;
                }
                leaseToRelease?.Dispose();
            }
        }

        private void StopMusicImmediate()
        {
            if (_musicSource != null)
            {
                _musicSource.Stop();
                _musicSource.clip = null;
            }
            _musicLease?.Dispose();
            _musicLease = null;
            _currentMusicAddress = null;
        }

        /// <summary>
        /// 播放音效并返回代际安全句柄。取消会立即结束调用；共享资源的迟到加载结果由 AssetLease 自动归还。
        /// </summary>
        public async UniTask<AudioPlaybackHandle> PlaySoundAsync(
            string address,
            Vector3 position = default,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(address))
                throw new ArgumentException("音效地址不能为空。", nameof(address));
            ThrowIfUnavailable();

            long id = ++_nextPlaybackId;
            CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _lifetimeCts.Token);
            _pendingSounds[id] = lifetime;

            AssetLease<AudioClip> lease = null;
            bool transferred = false;
            try
            {
                lease = await _assetProvider.LoadAsync(address, lifetime.Token);
                if (lease == null)
                {
                    GameLog.Error($"PlaySoundAsync: 加载音效失败 - {address}");
                    return default;
                }

                lifetime.Token.ThrowIfCancellationRequested();
                if (!_pendingSounds.Remove(id))
                    throw new OperationCanceledException(lifetime.Token);

                AudioSource source = _audioSourcePool.Get();
                if (source == null)
                {
                    GameLog.Error("PlaySoundAsync: 无法获取 AudioSource");
                    return default;
                }

                source.clip = lease.Asset;
                source.loop = false;
                source.volume = GetEffectiveSoundVolume();
                source.transform.position = position;
                source.spatialBlend = position == Vector3.zero ? 0f : 1f;

                var record = new PlaybackRecord
                {
                    Id = id,
                    Source = source,
                    Lease = lease,
                    Lifetime = lifetime,
                    State = AudioPlaybackState.Playing
                };
                _activeSounds.Add(id, record);
                lease = null;
                transferred = true;
                source.Play();
                return new AudioPlaybackHandle(this, id);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                GameLog.Error($"PlaySoundAsync: 播放音效异常 [{address}]：{ex.Message}");
                return default;
            }
            finally
            {
                lease?.Dispose();
                if (!transferred)
                {
                    _pendingSounds.Remove(id);
                    lifetime.Dispose();
                }
            }
        }

        public bool StopSound(AudioPlaybackHandle handle)
        {
            if (!Owns(handle))
                return false;
            return CompleteSound(handle.PlaybackId, CompletionReason.Stopped);
        }

        public bool PauseSound(AudioPlaybackHandle handle)
        {
            if (!Owns(handle) || !_activeSounds.TryGetValue(handle.PlaybackId, out PlaybackRecord record) ||
                record.State != AudioPlaybackState.Playing)
                return false;

            record.Source.Pause();
            record.State = AudioPlaybackState.Paused;
            return true;
        }

        public bool ResumeSound(AudioPlaybackHandle handle)
        {
            if (!Owns(handle) || !_activeSounds.TryGetValue(handle.PlaybackId, out PlaybackRecord record) ||
                record.State != AudioPlaybackState.Paused)
                return false;

            record.Source.UnPause();
            record.State = AudioPlaybackState.Playing;
            return true;
        }

        public void StopAllSounds() => StopAllSounds(CompletionReason.StopAll);

        private void StopAllSounds(CompletionReason reason)
        {
            if (_pendingSounds.Count > 0)
            {
                var pending = new List<CancellationTokenSource>(_pendingSounds.Values);
                for (int i = 0; i < pending.Count; i++)
                {
                    try { pending[i].Cancel(); } catch { }
                }
            }

            if (_activeSounds.Count == 0)
                return;

            _completionBuffer.Clear();
            _completionBuffer.AddRange(_activeSounds.Keys);
            for (int i = 0; i < _completionBuffer.Count; i++)
                CompleteSound(_completionBuffer[i], reason);
        }

        private bool CompleteSound(long id, CompletionReason reason)
        {
            if (!_activeSounds.TryGetValue(id, out PlaybackRecord record))
                return false;

            // 先从表中摘除：Stop/自然结束/Shutdown 同帧竞态只允许第一个终态释放所有权。
            _activeSounds.Remove(id);
            record.State = AudioPlaybackState.Completed;
            try { record.Lifetime.Cancel(); } catch { }

            if (record.Source != null)
            {
                record.Source.Stop();
                record.Source.clip = null;
                _audioSourcePool?.Release(record.Source);
            }

            record.Lease?.Dispose();
            record.Lease = null;
            record.Lifetime.Dispose();
            return true;
        }

        internal bool IsSoundActive(long playbackId) => _activeSounds.ContainsKey(playbackId);

        internal AudioPlaybackState GetSoundState(long playbackId) =>
            _activeSounds.TryGetValue(playbackId, out PlaybackRecord record)
                ? record.State
                : playbackId > 0 ? AudioPlaybackState.Completed : AudioPlaybackState.Invalid;

        private bool Owns(AudioPlaybackHandle handle) =>
            handle.IsOwnedBy(this) && handle.PlaybackId > 0 && _activeSounds.ContainsKey(handle.PlaybackId);

        private CancellationTokenSource BeginMusicOperation(CancellationToken callerToken)
        {
            CancelCurrentMusicOperation();
            CancellationTokenSource operation = CancellationTokenSource.CreateLinkedTokenSource(
                callerToken,
                _lifetimeCts?.Token ?? CancellationToken.None);
            _musicOperationCts = operation;
            return operation;
        }

        private void ThrowIfUnavailable()
        {
            if (_isShuttingDown || _lifetimeCts == null || _audioSourcePool == null)
                throw new InvalidOperationException("AudioManager 尚未初始化或已经关闭。");
        }

        private void CreateMusicSource()
        {
            var musicObject = new GameObject("MusicSource");
            if (Application.isPlaying)
                UnityEngine.Object.DontDestroyOnLoad(musicObject);
            _musicSource = musicObject.AddComponent<AudioSource>();
            _musicSource.playOnAwake = false;
            _musicSource.loop = true;
            _musicSource.volume = GetEffectiveMusicVolume();
        }

        private static async UniTask FadeVolumeAsync(
            AudioSource source,
            float targetVolume,
            float duration,
            CancellationToken cancellationToken)
        {
            if (source == null || duration <= 0f)
                return;

            float startVolume = source.volume;
            float elapsed = 0f;
            while (elapsed < duration)
            {
                cancellationToken.ThrowIfCancellationRequested();
                elapsed += Time.unscaledDeltaTime;
                source.volume = Mathf.Lerp(startVolume, targetVolume, Mathf.Clamp01(elapsed / duration));
                await UniTask.Yield(PlayerLoopTiming.Update, cancellationToken);
            }
            source.volume = targetVolume;
        }

        private void UpdateAllVolumes()
        {
            UpdateMusicVolume();
            UpdateSoundVolumes();
        }

        private void UpdateMusicVolume()
        {
            if (_musicSource != null)
                _musicSource.volume = GetEffectiveMusicVolume();
        }

        private void UpdateSoundVolumes()
        {
            float volume = GetEffectiveSoundVolume();
            foreach (PlaybackRecord record in _activeSounds.Values)
                if (record.Source != null) record.Source.volume = volume;
        }

        private float GetEffectiveMusicVolume() => _isMuted ? 0f : _masterVolume * _musicVolume;
        private float GetEffectiveSoundVolume() => _isMuted ? 0f : _masterVolume * _soundVolume;

        private void CancelCurrentMusicOperation()
        {
            CancellationTokenSource current = _musicOperationCts;
            _musicOperationCts = null;
            if (current == null) return;
            try { current.Cancel(); } catch { }
            // 所有权属于创建该 CTS 的异步操作；对应 finally 负责 Dispose，避免
            // 新操作在旧操作仍从 Token 退栈时提前释放其注册表。
        }

        private static void DestroyGameObject(GameObject gameObject)
        {
            if (gameObject == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(gameObject);
            else UnityEngine.Object.DestroyImmediate(gameObject);
        }

        internal sealed class AudioSourcePool
        {
            private readonly Queue<AudioSource> _pool = new Queue<AudioSource>();
            private readonly GameObject _poolRoot;

            public AudioSourcePool(int initialSize)
            {
                _poolRoot = new GameObject("AudioSourcePool");
                if (Application.isPlaying)
                    UnityEngine.Object.DontDestroyOnLoad(_poolRoot);
                for (int i = 0; i < initialSize; i++)
                    _pool.Enqueue(CreateAudioSource());
            }

            public AudioSource Get()
            {
                AudioSource source = _pool.Count > 0 ? _pool.Dequeue() : CreateAudioSource();
                source.gameObject.SetActive(true);
                return source;
            }

            public void Release(AudioSource source)
            {
                if (source == null) return;
                source.Stop();
                source.clip = null;
                source.loop = false;
                source.spatialBlend = 0f;
                source.volume = 1f;
                source.transform.localPosition = Vector3.zero;
                source.gameObject.SetActive(false);
                _pool.Enqueue(source);
            }

            public void Clear()
            {
                _pool.Clear();
                DestroyGameObject(_poolRoot);
            }

            private AudioSource CreateAudioSource()
            {
                var go = new GameObject("PooledAudioSource");
                go.transform.SetParent(_poolRoot.transform, false);
                var source = go.AddComponent<AudioSource>();
                source.playOnAwake = false;
                go.SetActive(false);
                return source;
            }
        }
    }
}
