using System;

namespace Framework
{
    /// <summary>音效播放状态。状态由 AudioManager 独占维护，调用方只能经 Handle 发出控制请求。</summary>
    public enum AudioPlaybackState
    {
        Invalid = 0,
        Playing = 1,
        Paused = 2,
        Completed = 3
    }

    /// <summary>
    /// 代际安全的音效控制句柄。它不暴露池化 AudioSource，旧句柄因此无法误操作已经被复用的新播放。
    /// </summary>
    public readonly struct AudioPlaybackHandle : IEquatable<AudioPlaybackHandle>
    {
        private readonly AudioManager _owner;

        internal AudioPlaybackHandle(AudioManager owner, long playbackId)
        {
            _owner = owner;
            PlaybackId = playbackId;
        }

        /// <summary>进程内单调递增的播放 ID。</summary>
        public long PlaybackId { get; }

        /// <summary>句柄是否仍对应活动播放。</summary>
        public bool IsValid => _owner != null && _owner.IsSoundActive(PlaybackId);

        /// <summary>当前状态；播放已经结束时返回 Completed，无效默认值返回 Invalid。</summary>
        public AudioPlaybackState State => _owner == null
            ? AudioPlaybackState.Invalid
            : _owner.GetSoundState(PlaybackId);

        public bool Stop() => _owner != null && _owner.StopSound(this);
        public bool Pause() => _owner != null && _owner.PauseSound(this);
        public bool Resume() => _owner != null && _owner.ResumeSound(this);

        internal bool IsOwnedBy(AudioManager owner) => ReferenceEquals(_owner, owner);

        public bool Equals(AudioPlaybackHandle other) =>
            ReferenceEquals(_owner, other._owner) && PlaybackId == other.PlaybackId;

        public override bool Equals(object obj) => obj is AudioPlaybackHandle other && Equals(other);
        public override int GetHashCode() => ((_owner != null ? _owner.GetHashCode() : 0) * 397) ^ PlaybackId.GetHashCode();
        public static bool operator ==(AudioPlaybackHandle left, AudioPlaybackHandle right) => left.Equals(right);
        public static bool operator !=(AudioPlaybackHandle left, AudioPlaybackHandle right) => !left.Equals(right);
    }
}
