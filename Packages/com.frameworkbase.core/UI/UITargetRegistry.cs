using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace Framework
{
    /// <summary>当前处于激活状态的语义 UI 目标；TargetId 来自配置生成常量。</summary>
    public sealed class UITarget
    {
        internal UITarget(int targetId, RectTransform rectTransform, Button button, object scope)
        {
            TargetId = targetId;
            RectTransform = rectTransform;
            Button = button;
            Scope = scope;
        }

        public int TargetId { get; }
        public RectTransform RectTransform { get; }
        public Button Button { get; }
        public object Scope { get; }
        public GameObject GameObject => RectTransform != null ? RectTransform.gameObject : null;
    }

    /// <summary>
    /// 稳定 TargetId 到当前 UI 实例的运行时目录。注册与点击订阅都返回作用域句柄；
    /// 同一 TargetId 可在多个窗口实例中同时存在，Scope 用于消除歧义。
    /// </summary>
    public sealed class UITargetRegistry
    {
        private sealed class Registration : IDisposable
        {
            private UITargetRegistry _owner;
            public readonly UITarget Target;
            public readonly Action ClickForwarder;

            public Registration(UITargetRegistry owner, UITarget target)
            {
                _owner = owner;
                Target = target;
                ClickForwarder = () => owner.NotifyClick(target);
            }

            public void Dispose()
            {
                UITargetRegistry owner = _owner;
                if (owner == null) return;
                _owner = null;
                owner.Remove(this);
            }
        }

        private sealed class ClickSubscription : IDisposable
        {
            private UITargetRegistry _owner;
            public readonly int TargetId;
            public readonly object Scope;
            public readonly Action<UITarget> Handler;
            public bool IsDisposed { get; private set; }

            public ClickSubscription(
                UITargetRegistry owner,
                int targetId,
                object scope,
                Action<UITarget> handler)
            {
                _owner = owner;
                TargetId = targetId;
                Scope = scope;
                Handler = handler;
            }

            public void Dispose()
            {
                if (IsDisposed) return;
                IsDisposed = true;
                UITargetRegistry owner = _owner;
                _owner = null;
                owner?.RemoveSubscription(this);
            }
        }

        private readonly Dictionary<int, List<Registration>> _targets =
            new Dictionary<int, List<Registration>>();
        private readonly Dictionary<int, List<ClickSubscription>> _clickSubscriptions =
            new Dictionary<int, List<ClickSubscription>>();
        private int _notifyDepth;

        public Action<Exception> ObserverErrorSink { get; set; }

        public IDisposable Register(
            int targetId,
            RectTransform rectTransform,
            Button button = null,
            object scope = null)
        {
            if (targetId <= 0) throw new ArgumentOutOfRangeException(nameof(targetId));
            if (rectTransform == null) throw new ArgumentNullException(nameof(rectTransform));
            if (!_targets.TryGetValue(targetId, out List<Registration> entries))
            {
                entries = new List<Registration>();
                _targets.Add(targetId, entries);
            }

            for (int i = 0; i < entries.Count; i++)
            {
                UITarget existing = entries[i].Target;
                if (ReferenceEquals(existing.Scope, scope))
                    throw new InvalidOperationException(
                        $"UI TargetId={targetId} 在同一 Scope 中重复激活：{rectTransform.name}。");
            }

            var target = new UITarget(targetId, rectTransform, button, scope);
            var registration = new Registration(this, target);
            entries.Add(registration);
            if (button != null) button.onClick.AddListener(registration.ClickForwarder.Invoke);
            return registration;
        }

        /// <summary>
        /// 解析唯一目标。传 Scope 时按引用精确匹配；不传 Scope 且同时存在多个实例时返回 false，禁止静默选错。
        /// </summary>
        public bool TryResolve(int targetId, object scope, out UITarget target)
        {
            target = null;
            if (!_targets.TryGetValue(targetId, out List<Registration> entries) || entries.Count == 0)
                return false;
            if (scope == null)
            {
                if (entries.Count != 1) return false;
                target = entries[0].Target;
                return true;
            }
            for (int i = 0; i < entries.Count; i++)
            {
                if (!ReferenceEquals(entries[i].Target.Scope, scope)) continue;
                target = entries[i].Target;
                return true;
            }
            return false;
        }

        public bool TryResolve(int targetId, out UITarget target)
            => TryResolve(targetId, null, out target);

        public UITarget Resolve(int targetId, object scope = null)
        {
            if (TryResolve(targetId, scope, out UITarget target)) return target;
            int count = _targets.TryGetValue(targetId, out List<Registration> entries) ? entries.Count : 0;
            throw new InvalidOperationException(
                count > 1 && scope == null
                    ? $"UI TargetId={targetId} 同时存在 {count} 个实例，必须提供 Scope。"
                    : $"UI TargetId={targetId} 当前未激活或不属于指定 Scope。");
        }

        /// <summary>订阅语义目标点击；目标尚未激活时订阅仍保留，后续实例注册后自动生效。</summary>
        public IDisposable SubscribeClick(int targetId, object scope, Action<UITarget> handler)
        {
            if (targetId <= 0) throw new ArgumentOutOfRangeException(nameof(targetId));
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            if (!_clickSubscriptions.TryGetValue(targetId, out List<ClickSubscription> list))
            {
                list = new List<ClickSubscription>();
                _clickSubscriptions.Add(targetId, list);
            }
            var subscription = new ClickSubscription(this, targetId, scope, handler);
            list.Add(subscription);
            return subscription;
        }

        public IDisposable SubscribeClick(int targetId, Action<UITarget> handler)
            => SubscribeClick(targetId, null, handler);

        public void Clear()
        {
            var ids = new List<int>(_targets.Keys);
            for (int n = 0; n < ids.Count; n++)
            {
                if (!_targets.TryGetValue(ids[n], out List<Registration> list)) continue;
                for (int i = list.Count - 1; i >= 0; i--) list[i].Dispose();
            }
            _targets.Clear();
            _clickSubscriptions.Clear();
            _notifyDepth = 0;
        }

        private void NotifyClick(UITarget target)
        {
            if (!_clickSubscriptions.TryGetValue(target.TargetId, out List<ClickSubscription> list)) return;
            _notifyDepth++;
            try
            {
                int count = list.Count;
                for (int i = 0; i < count; i++)
                {
                    ClickSubscription subscription = list[i];
                    if (subscription.IsDisposed) continue;
                    if (subscription.Scope != null && !ReferenceEquals(subscription.Scope, target.Scope)) continue;
                    try { subscription.Handler(target); }
                    catch (Exception ex) { Report(ex); }
                }
            }
            finally
            {
                _notifyDepth--;
                if (_notifyDepth == 0) Compact(list, target.TargetId);
            }
        }

        private void Remove(Registration registration)
        {
            UITarget target = registration.Target;
            if (target.Button != null)
                target.Button.onClick.RemoveListener(registration.ClickForwarder.Invoke);
            if (!_targets.TryGetValue(target.TargetId, out List<Registration> list)) return;
            list.Remove(registration);
            if (list.Count == 0) _targets.Remove(target.TargetId);
        }

        private void RemoveSubscription(ClickSubscription subscription)
        {
            if (_notifyDepth > 0) return;
            if (!_clickSubscriptions.TryGetValue(subscription.TargetId, out List<ClickSubscription> list)) return;
            list.Remove(subscription);
            if (list.Count == 0) _clickSubscriptions.Remove(subscription.TargetId);
        }

        private void Compact(List<ClickSubscription> list, int targetId)
        {
            for (int i = list.Count - 1; i >= 0; i--)
                if (list[i].IsDisposed) list.RemoveAt(i);
            if (list.Count == 0) _clickSubscriptions.Remove(targetId);
        }

        private void Report(Exception error)
        {
            try { ObserverErrorSink?.Invoke(error); }
            catch { }
        }
    }
}
