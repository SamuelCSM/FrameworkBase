using System;
using System.Collections.Generic;
using UnityEngine;
using Cysharp.Threading.Tasks;

namespace Framework
{
    /// <summary>
    /// 音频管理器
    /// 管理背景音乐和音效播放，支持音量控制、淡入淡出、对象池等功能
    /// </summary>
    public class AudioManager : Core.FrameworkComponent
    {
        // 音频源对象池
        private AudioSourcePool _audioSourcePool;
        
        // 背景音乐音频源
        private AudioSource _musicSource;
        
        // 当前播放的音乐地址
        private string _currentMusicAddress;
        
        // 正在播放的音效列表
        private List<AudioSource> _activeSounds = new List<AudioSource>();
        
        // 音量设置
        private float _masterVolume = 1f;
        private float _musicVolume = 1f;
        private float _soundVolume = 1f;
        private bool _isMuted = false;
        
        // 淡入淡出协程取消令牌
        private System.Threading.CancellationTokenSource _fadeCts;

        #region 属性

        /// <summary>
        /// 主音量 (0-1)
        /// </summary>
        public float MasterVolume
        {
            get => _masterVolume;
            set
            {
                _masterVolume = Mathf.Clamp01(value);
                UpdateAllVolumes();
            }
        }

        /// <summary>
        /// 音乐音量 (0-1)
        /// </summary>
        public float MusicVolume
        {
            get => _musicVolume;
            set
            {
                _musicVolume = Mathf.Clamp01(value);
                UpdateMusicVolume();
            }
        }

        /// <summary>
        /// 音效音量 (0-1)
        /// </summary>
        public float SoundVolume
        {
            get => _soundVolume;
            set
            {
                _soundVolume = Mathf.Clamp01(value);
                UpdateSoundVolumes();
            }
        }

        /// <summary>
        /// 是否静音
        /// </summary>
        public bool IsMuted
        {
            get => _isMuted;
            set
            {
                _isMuted = value;
                UpdateAllVolumes();
            }
        }

        #endregion

        #region 生命周期

        public override void OnInit()
        {
            Logger.Log("AudioManager 初始化");
            
            // 创建音频源对象池
            _audioSourcePool = new AudioSourcePool(10);
            
            // 创建背景音乐音频源
            CreateMusicSource();
        }

        public override void OnUpdate(float deltaTime)
        {
            // 清理已播放完成的音效
            for (int i = _activeSounds.Count - 1; i >= 0; i--)
            {
                if (_activeSounds[i] == null || !_activeSounds[i].isPlaying)
                {
                    if (_activeSounds[i] != null)
                    {
                        _audioSourcePool.Release(_activeSounds[i]);
                    }
                    _activeSounds.RemoveAt(i);
                }
            }
        }

        public override void OnShutdown()
        {
            // 停止所有音频
            StopMusic(0f);
            StopAllSounds();
            
            // 取消淡入淡出
            _fadeCts?.Cancel();
            _fadeCts?.Dispose();
            
            // 清理对象池
            _audioSourcePool?.Clear();
            
            // 销毁音乐源
            if (_musicSource != null)
            {
                UnityEngine.Object.Destroy(_musicSource.gameObject);
                _musicSource = null;
            }
            
            Logger.Log("AudioManager 关闭");
        }

        #endregion

        #region 背景音乐

        /// <summary>
        /// 播放背景音乐
        /// </summary>
        /// <param name="address">音频资源地址</param>
        /// <param name="loop">是否循环</param>
        /// <param name="fadeTime">淡入时间（秒）</param>
        public async UniTask PlayMusicAsync(string address, bool loop = true, float fadeTime = 0f)
        {
            if (string.IsNullOrEmpty(address))
            {
                Logger.Error("PlayMusicAsync: address 不能为空");
                return;
            }

            // 如果正在播放相同的音乐，不做处理
            if (_currentMusicAddress == address && _musicSource.isPlaying)
            {
                Logger.Log($"PlayMusicAsync: 音乐已在播放 - {address}");
                return;
            }

            // 停止当前音乐
            if (_musicSource.isPlaying)
            {
                await StopMusicInternal(fadeTime);
            }

            // 加载音频资源
            var audioClip = await Core.GameEntry.Resource.LoadAssetAsync<AudioClip>(address);
            if (audioClip == null)
            {
                Logger.Error($"PlayMusicAsync: 加载音频失败 - {address}");
                return;
            }

            // 设置音频源
            _musicSource.clip = audioClip;
            _musicSource.loop = loop;
            _currentMusicAddress = address;

            // 播放音乐
            if (fadeTime > 0f)
            {
                // 淡入播放
                _musicSource.volume = 0f;
                _musicSource.Play();
                await FadeVolume(_musicSource, GetEffectiveMusicVolume(), fadeTime);
            }
            else
            {
                // 直接播放
                _musicSource.volume = GetEffectiveMusicVolume();
                _musicSource.Play();
            }

            Logger.Log($"PlayMusicAsync: 播放音乐 - {address}");
        }

        /// <summary>
        /// 停止背景音乐
        /// </summary>
        /// <param name="fadeTime">淡出时间（秒）</param>
        public void StopMusic(float fadeTime = 0f)
        {
            StopMusicInternal(fadeTime).Forget();
        }

        /// <summary>
        /// 暂停背景音乐
        /// </summary>
        public void PauseMusic()
        {
            if (_musicSource != null && _musicSource.isPlaying)
            {
                _musicSource.Pause();
                Logger.Log("PauseMusic: 暂停音乐");
            }
        }

        /// <summary>
        /// 恢复背景音乐
        /// </summary>
        public void ResumeMusic()
        {
            if (_musicSource != null && _musicSource.clip != null)
            {
                _musicSource.UnPause();
                Logger.Log("ResumeMusic: 恢复音乐");
            }
        }

        /// <summary>
        /// 内部停止音乐方法
        /// </summary>
        private async UniTask StopMusicInternal(float fadeTime)
        {
            if (_musicSource == null || !_musicSource.isPlaying)
            {
                return;
            }

            if (fadeTime > 0f)
            {
                // 淡出停止
                await FadeVolume(_musicSource, 0f, fadeTime);
            }

            _musicSource.Stop();
            _musicSource.clip = null;

            // 释放音频资源
            if (!string.IsNullOrEmpty(_currentMusicAddress))
            {
                Core.GameEntry.Resource.ReleaseAsset(_currentMusicAddress);
                _currentMusicAddress = null;
            }

            Logger.Log("StopMusicInternal: 停止音乐");
        }

        #endregion

        #region 音效

        /// <summary>
        /// 播放音效
        /// </summary>
        /// <param name="address">音频资源地址</param>
        /// <param name="position">3D音效位置（默认为零向量表示2D音效）</param>
        /// <returns>音频源（可用于控制音效）</returns>
        public async UniTask<AudioSource> PlaySoundAsync(string address, Vector3 position = default)
        {
            if (string.IsNullOrEmpty(address))
            {
                Logger.Error("PlaySoundAsync: address 不能为空");
                return null;
            }

            // 加载音频资源
            var audioClip = await Core.GameEntry.Resource.LoadAssetAsync<AudioClip>(address);
            if (audioClip == null)
            {
                Logger.Error($"PlaySoundAsync: 加载音频失败 - {address}");
                return null;
            }

            // 从对象池获取音频源
            var audioSource = _audioSourcePool.Get();
            if (audioSource == null)
            {
                Logger.Error("PlaySoundAsync: 无法获取音频源");
                Core.GameEntry.Resource.ReleaseAsset(address);
                return null;
            }

            // 设置音频源
            audioSource.clip = audioClip;
            audioSource.loop = false;
            audioSource.volume = GetEffectiveSoundVolume();

            // 设置3D音效
            if (position != Vector3.zero)
            {
                audioSource.transform.position = position;
                audioSource.spatialBlend = 1f; // 3D音效
            }
            else
            {
                audioSource.spatialBlend = 0f; // 2D音效
            }

            // 播放音效
            audioSource.Play();
            _activeSounds.Add(audioSource);

            Logger.Log($"PlaySoundAsync: 播放音效 - {address}");
            return audioSource;
        }

        /// <summary>
        /// 停止指定音效
        /// </summary>
        /// <param name="source">音频源</param>
        public void StopSound(AudioSource source)
        {
            if (source == null)
            {
                return;
            }

            source.Stop();
            _activeSounds.Remove(source);
            _audioSourcePool.Release(source);

            Logger.Log("StopSound: 停止音效");
        }

        /// <summary>
        /// 停止所有音效
        /// </summary>
        public void StopAllSounds()
        {
            foreach (var source in _activeSounds)
            {
                if (source != null)
                {
                    source.Stop();
                    _audioSourcePool.Release(source);
                }
            }

            _activeSounds.Clear();
            Logger.Log("StopAllSounds: 停止所有音效");
        }

        #endregion

        #region 私有方法

        /// <summary>
        /// 创建背景音乐音频源
        /// </summary>
        private void CreateMusicSource()
        {
            var musicObject = new GameObject("MusicSource");
            UnityEngine.Object.DontDestroyOnLoad(musicObject);
            
            _musicSource = musicObject.AddComponent<AudioSource>();
            _musicSource.playOnAwake = false;
            _musicSource.loop = true;
            _musicSource.volume = GetEffectiveMusicVolume();
        }

        /// <summary>
        /// 音量淡入淡出
        /// </summary>
        private async UniTask FadeVolume(AudioSource source, float targetVolume, float duration)
        {
            if (source == null || duration <= 0f)
            {
                return;
            }

            // 取消之前的淡入淡出
            _fadeCts?.Cancel();
            _fadeCts?.Dispose();
            _fadeCts = new System.Threading.CancellationTokenSource();

            float startVolume = source.volume;
            float elapsed = 0f;

            try
            {
                while (elapsed < duration)
                {
                    elapsed += Time.deltaTime;
                    float t = elapsed / duration;
                    source.volume = Mathf.Lerp(startVolume, targetVolume, t);
                    await UniTask.Yield(_fadeCts.Token);
                }

                source.volume = targetVolume;
            }
            catch (OperationCanceledException)
            {
                // 淡入淡出被取消
            }
        }

        /// <summary>
        /// 更新所有音量
        /// </summary>
        private void UpdateAllVolumes()
        {
            UpdateMusicVolume();
            UpdateSoundVolumes();
        }

        /// <summary>
        /// 更新音乐音量
        /// </summary>
        private void UpdateMusicVolume()
        {
            if (_musicSource != null)
            {
                _musicSource.volume = GetEffectiveMusicVolume();
            }
        }

        /// <summary>
        /// 更新音效音量
        /// </summary>
        private void UpdateSoundVolumes()
        {
            float effectiveVolume = GetEffectiveSoundVolume();
            foreach (var source in _activeSounds)
            {
                if (source != null)
                {
                    source.volume = effectiveVolume;
                }
            }
        }

        /// <summary>
        /// 获取有效的音乐音量
        /// </summary>
        private float GetEffectiveMusicVolume()
        {
            return _isMuted ? 0f : _masterVolume * _musicVolume;
        }

        /// <summary>
        /// 获取有效的音效音量
        /// </summary>
        private float GetEffectiveSoundVolume()
        {
            return _isMuted ? 0f : _masterVolume * _soundVolume;
        }

        #endregion
    }

    /// <summary>
    /// 音频源对象池
    /// </summary>
    internal class AudioSourcePool
    {
        private Queue<AudioSource> _pool = new Queue<AudioSource>();
        private GameObject _poolRoot;
        private int _initialSize;

        public AudioSourcePool(int initialSize)
        {
            _initialSize = initialSize;
            
            // 创建对象池根节点
            _poolRoot = new GameObject("AudioSourcePool");
            UnityEngine.Object.DontDestroyOnLoad(_poolRoot);

            // 预热对象池
            for (int i = 0; i < initialSize; i++)
            {
                CreateAudioSource();
            }
        }

        /// <summary>
        /// 从对象池获取音频源
        /// </summary>
        public AudioSource Get()
        {
            AudioSource source;
            
            if (_pool.Count > 0)
            {
                source = _pool.Dequeue();
            }
            else
            {
                source = CreateAudioSource();
            }

            if (source != null)
            {
                source.gameObject.SetActive(true);
            }

            return source;
        }

        /// <summary>
        /// 归还音频源到对象池
        /// </summary>
        public void Release(AudioSource source)
        {
            if (source == null)
            {
                return;
            }

            source.Stop();
            source.clip = null;
            source.gameObject.SetActive(false);
            source.transform.SetParent(_poolRoot.transform);
            
            _pool.Enqueue(source);
        }

        /// <summary>
        /// 清理对象池
        /// </summary>
        public void Clear()
        {
            while (_pool.Count > 0)
            {
                var source = _pool.Dequeue();
                if (source != null)
                {
                    UnityEngine.Object.Destroy(source.gameObject);
                }
            }

            if (_poolRoot != null)
            {
                UnityEngine.Object.Destroy(_poolRoot);
                _poolRoot = null;
            }
        }

        /// <summary>
        /// 创建新的音频源
        /// </summary>
        private AudioSource CreateAudioSource()
        {
            var go = new GameObject($"AudioSource_{_pool.Count}");
            go.transform.SetParent(_poolRoot.transform);
            go.SetActive(false);

            var source = go.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.loop = false;

            _pool.Enqueue(source);
            return source;
        }
    }
}
