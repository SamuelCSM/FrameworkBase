using UnityEngine;
using Cysharp.Threading.Tasks;
using System;
using System.Threading;

namespace Framework
{
    /// <summary>
    /// UniTask工具类
    /// 提供常用的异步操作封装，简化UniTask的使用
    /// </summary>
    public static class UniTaskHelper
{
    /// <summary>
    /// 在下一帧Update时执行
    /// </summary>
    /// <param name="action">要执行的操作</param>
    /// <param name="cancellationToken">取消令牌</param>
    public static async UniTask NextFrameUpdate(Action action, CancellationToken cancellationToken = default)
    {
        await UniTask.Yield(PlayerLoopTiming.Update, cancellationToken);
        action?.Invoke();
    }

    /// <summary>
    /// 在下一帧LateUpdate时执行
    /// </summary>
    /// <param name="action">要执行的操作</param>
    /// <param name="cancellationToken">取消令牌</param>
    public static async UniTask NextFrameLateUpdate(Action action, CancellationToken cancellationToken = default)
    {
        await UniTask.Yield(PlayerLoopTiming.LastPostLateUpdate, cancellationToken);
        action?.Invoke();
    }

    /// <summary>
    /// 在下一帧FixedUpdate时执行
    /// </summary>
    /// <param name="action">要执行的操作</param>
    /// <param name="cancellationToken">取消令牌</param>
    public static async UniTask NextFrameFixedUpdate(Action action, CancellationToken cancellationToken = default)
    {
        await UniTask.Yield(PlayerLoopTiming.FixedUpdate, cancellationToken);
        action?.Invoke();
    }

    /// <summary>
    /// 延迟执行（毫秒）
    /// </summary>
    /// <param name="action">要执行的操作</param>
    /// <param name="delayMilliseconds">延迟时间（毫秒）</param>
    /// <param name="ignoreTimeScale">是否忽略时间缩放</param>
    /// <param name="cancellationToken">取消令牌</param>
    public static async UniTask DelayAction(Action action, int delayMilliseconds, bool ignoreTimeScale = false, CancellationToken cancellationToken = default)
    {
        await UniTask.Delay(delayMilliseconds, ignoreTimeScale, cancellationToken: cancellationToken);
        action?.Invoke();
    }

    /// <summary>
    /// 延迟执行（秒）
    /// </summary>
    /// <param name="action">要执行的操作</param>
    /// <param name="delaySeconds">延迟时间（秒）</param>
    /// <param name="ignoreTimeScale">是否忽略时间缩放</param>
    /// <param name="cancellationToken">取消令牌</param>
    public static async UniTask DelayActionSeconds(Action action, float delaySeconds, bool ignoreTimeScale = false, CancellationToken cancellationToken = default)
    {
        int delayMilliseconds = Mathf.RoundToInt(delaySeconds * 1000);
        await UniTask.Delay(delayMilliseconds, ignoreTimeScale, cancellationToken: cancellationToken);
        action?.Invoke();
    }

    /// <summary>
    /// 每帧执行任务（直到取消）
    /// </summary>
    /// <param name="action">每帧要执行的操作</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <param name="timing">执行时机</param>
    public static async UniTask EveryFrame(Action action, CancellationToken cancellationToken, PlayerLoopTiming timing = PlayerLoopTiming.Update)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            action?.Invoke();
            await UniTask.Yield(timing, cancellationToken);
        }
    }

    /// <summary>
    /// 每帧执行任务（带帧计数，执行指定次数）
    /// </summary>
    /// <param name="action">每帧要执行的操作（参数为当前帧数）</param>
    /// <param name="frameCount">执行帧数</param>
    /// <param name="timing">执行时机</param>
    /// <param name="cancellationToken">取消令牌</param>
    public static async UniTask EveryFrameCount(Action<int> action, int frameCount, PlayerLoopTiming timing = PlayerLoopTiming.Update, CancellationToken cancellationToken = default)
    {
        for (int i = 0; i < frameCount; i++)
        {
            if (cancellationToken.IsCancellationRequested)
                break;
                
            action?.Invoke(i);
            await UniTask.Yield(timing, cancellationToken);
        }
    }

    /// <summary>
    /// 每隔指定秒数执行任务（循环执行直到取消）
    /// </summary>
    /// <param name="action">要执行的操作</param>
    /// <param name="intervalSeconds">间隔时间（秒）</param>
    /// <param name="ignoreTimeScale">是否忽略时间缩放</param>
    /// <param name="cancellationToken">取消令牌</param>
    public static async UniTask EveryInterval(Action action, float intervalSeconds, bool ignoreTimeScale = false, CancellationToken cancellationToken = default)
    {
        int intervalMilliseconds = Mathf.RoundToInt(intervalSeconds * 1000);
        
        while (!cancellationToken.IsCancellationRequested)
        {
            action?.Invoke();
            await UniTask.Delay(intervalMilliseconds, ignoreTimeScale, cancellationToken: cancellationToken);
        }
    }

    /// <summary>
    /// 每隔指定秒数执行任务（执行指定次数）
    /// </summary>
    /// <param name="action">要执行的操作（参数为当前执行次数）</param>
    /// <param name="intervalSeconds">间隔时间（秒）</param>
    /// <param name="repeatCount">重复次数</param>
    /// <param name="ignoreTimeScale">是否忽略时间缩放</param>
    /// <param name="cancellationToken">取消令牌</param>
    public static async UniTask EveryIntervalCount(Action<int> action, float intervalSeconds, int repeatCount, bool ignoreTimeScale = false, CancellationToken cancellationToken = default)
    {
        int intervalMilliseconds = Mathf.RoundToInt(intervalSeconds * 1000);
        
        for (int i = 0; i < repeatCount; i++)
        {
            if (cancellationToken.IsCancellationRequested)
                break;
                
            action?.Invoke(i);
            
            if (i < repeatCount - 1) // 最后一次不需要等待
            {
                await UniTask.Delay(intervalMilliseconds, ignoreTimeScale, cancellationToken: cancellationToken);
            }
        }
    }

    /// <summary>
    /// 等待条件满足
    /// </summary>
    /// <param name="predicate">条件判断函数</param>
    /// <param name="timing">检查时机</param>
    /// <param name="cancellationToken">取消令牌</param>
    public static async UniTask WaitUntil(Func<bool> predicate, PlayerLoopTiming timing = PlayerLoopTiming.Update, CancellationToken cancellationToken = default)
    {
        await UniTask.WaitUntil(predicate, timing, cancellationToken);
    }

    /// <summary>
    /// 等待条件不满足
    /// </summary>
    /// <param name="predicate">条件判断函数</param>
    /// <param name="timing">检查时机</param>
    /// <param name="cancellationToken">取消令牌</param>
    public static async UniTask WaitWhile(Func<bool> predicate, PlayerLoopTiming timing = PlayerLoopTiming.Update, CancellationToken cancellationToken = default)
    {
        await UniTask.WaitWhile(predicate, timing, cancellationToken);
    }

    /// <summary>
    /// 等待指定帧数
    /// </summary>
    /// <param name="frameCount">帧数</param>
    /// <param name="timing">执行时机</param>
    /// <param name="cancellationToken">取消令牌</param>
    public static async UniTask WaitForFrames(int frameCount, PlayerLoopTiming timing = PlayerLoopTiming.Update, CancellationToken cancellationToken = default)
    {
        for (int i = 0; i < frameCount; i++)
        {
            await UniTask.Yield(timing, cancellationToken);
        }
    }

    /// <summary>
    /// 超时执行（如果超时则抛出异常）
    /// </summary>
    /// <param name="task">要执行的任务</param>
    /// <param name="timeoutSeconds">超时时间（秒）</param>
    /// <param name="ignoreTimeScale">是否忽略时间缩放</param>
    public static async UniTask WithTimeout(UniTask task, float timeoutSeconds, bool ignoreTimeScale = false)
    {
        var timeoutTask = UniTask.Delay(Mathf.RoundToInt(timeoutSeconds * 1000), ignoreTimeScale);
        var completedTask = await UniTask.WhenAny(task, timeoutTask);
        
        if (completedTask == 1)
        {
            throw new TimeoutException($"任务执行超时（{timeoutSeconds}秒）");
        }
    }

    /// <summary>
    /// 超时执行（如果超时则返回false）
    /// </summary>
    /// <param name="task">要执行的任务</param>
    /// <param name="timeoutSeconds">超时时间（秒）</param>
    /// <param name="ignoreTimeScale">是否忽略时间缩放</param>
    /// <returns>是否在超时前完成</returns>
    public static async UniTask<bool> TryWithTimeout(UniTask task, float timeoutSeconds, bool ignoreTimeScale = false)
    {
        var timeoutTask = UniTask.Delay(Mathf.RoundToInt(timeoutSeconds * 1000), ignoreTimeScale);
        var completedTask = await UniTask.WhenAny(task, timeoutTask);
        return completedTask == 0;
    }

    /// <summary>
    /// 重试执行（失败后自动重试）
    /// </summary>
    /// <param name="taskFactory">任务工厂函数</param>
    /// <param name="maxRetryCount">最大重试次数</param>
    /// <param name="retryDelaySeconds">重试间隔（秒）</param>
    /// <param name="cancellationToken">取消令牌</param>
    public static async UniTask RetryAsync(Func<UniTask> taskFactory, int maxRetryCount = 3, float retryDelaySeconds = 1f, CancellationToken cancellationToken = default)
    {
        int retryCount = 0;
        Exception lastException = null;

        while (retryCount <= maxRetryCount)
        {
            try
            {
                await taskFactory();
                return; // 成功执行，退出
            }
            catch (Exception ex)
            {
                lastException = ex;
                retryCount++;

                if (retryCount > maxRetryCount)
                {
                    Debug.LogError($"重试{maxRetryCount}次后仍然失败: {ex.Message}");
                    throw;
                }

                Debug.LogWarning($"任务执行失败，{retryDelaySeconds}秒后进行第{retryCount}次重试: {ex.Message}");
                await UniTask.Delay(Mathf.RoundToInt(retryDelaySeconds * 1000), cancellationToken: cancellationToken);
            }
        }
    }

    /// <summary>
    /// 重试执行（带返回值）
    /// </summary>
    /// <typeparam name="T">返回值类型</typeparam>
    /// <param name="taskFactory">任务工厂函数</param>
    /// <param name="maxRetryCount">最大重试次数</param>
    /// <param name="retryDelaySeconds">重试间隔（秒）</param>
    /// <param name="cancellationToken">取消令牌</param>
    public static async UniTask<T> RetryAsync<T>(Func<UniTask<T>> taskFactory, int maxRetryCount = 3, float retryDelaySeconds = 1f, CancellationToken cancellationToken = default)
    {
        int retryCount = 0;
        Exception lastException = null;

        while (retryCount <= maxRetryCount)
        {
            try
            {
                return await taskFactory();
            }
            catch (Exception ex)
            {
                lastException = ex;
                retryCount++;

                if (retryCount > maxRetryCount)
                {
                    Debug.LogError($"重试{maxRetryCount}次后仍然失败: {ex.Message}");
                    throw;
                }

                Debug.LogWarning($"任务执行失败，{retryDelaySeconds}秒后进行第{retryCount}次重试: {ex.Message}");
                await UniTask.Delay(Mathf.RoundToInt(retryDelaySeconds * 1000), cancellationToken: cancellationToken);
            }
        }

        throw lastException;
    }

    // ==================== 不需要等待的便捷方法 ====================
    // 以下方法内部自动调用 .Forget()，可以直接调用，无需 await

    /// <summary>
    /// 在下一帧Update时执行（不等待）
    /// </summary>
    /// <param name="action">要执行的操作</param>
    /// <param name="cancellationToken">取消令牌</param>
    public static void RunNextFrameUpdate(Action action, CancellationToken cancellationToken = default)
    {
        NextFrameUpdate(action, cancellationToken).Forget();
    }

    /// <summary>
    /// 在下一帧LateUpdate时执行（不等待）
    /// </summary>
    /// <param name="action">要执行的操作</param>
    /// <param name="cancellationToken">取消令牌</param>
    public static void RunNextFrameLateUpdate(Action action, CancellationToken cancellationToken = default)
    {
        NextFrameLateUpdate(action, cancellationToken).Forget();
    }

    /// <summary>
    /// 在下一帧FixedUpdate时执行（不等待）
    /// </summary>
    /// <param name="action">要执行的操作</param>
    /// <param name="cancellationToken">取消令牌</param>
    public static void RunNextFrameFixedUpdate(Action action, CancellationToken cancellationToken = default)
    {
        NextFrameFixedUpdate(action, cancellationToken).Forget();
    }

    /// <summary>
    /// 延迟执行（毫秒）（不等待）
    /// </summary>
    /// <param name="action">要执行的操作</param>
    /// <param name="delayMilliseconds">延迟时间（毫秒）</param>
    /// <param name="ignoreTimeScale">是否忽略时间缩放</param>
    /// <param name="cancellationToken">取消令牌</param>
    public static void RunDelayAction(Action action, int delayMilliseconds, bool ignoreTimeScale = false, CancellationToken cancellationToken = default)
    {
        DelayAction(action, delayMilliseconds, ignoreTimeScale, cancellationToken).Forget();
    }

    /// <summary>
    /// 延迟执行（秒）（不等待）
    /// </summary>
    /// <param name="action">要执行的操作</param>
    /// <param name="delaySeconds">延迟时间（秒）</param>
    /// <param name="ignoreTimeScale">是否忽略时间缩放</param>
    /// <param name="cancellationToken">取消令牌</param>
    public static void RunDelayActionSeconds(Action action, float delaySeconds, bool ignoreTimeScale = false, CancellationToken cancellationToken = default)
    {
        DelayActionSeconds(action, delaySeconds, ignoreTimeScale, cancellationToken).Forget();
    }

    /// <summary>
    /// 每帧执行任务（直到取消）（不等待）
    /// </summary>
    /// <param name="action">每帧要执行的操作</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <param name="timing">执行时机</param>
    public static void RunEveryFrame(Action action, CancellationToken cancellationToken, PlayerLoopTiming timing = PlayerLoopTiming.Update)
    {
        EveryFrame(action, cancellationToken, timing).Forget();
    }

    /// <summary>
    /// 每帧执行任务（带帧计数，执行指定次数）（不等待）
    /// </summary>
    /// <param name="action">每帧要执行的操作（参数为当前帧数）</param>
    /// <param name="frameCount">执行帧数</param>
    /// <param name="timing">执行时机</param>
    /// <param name="cancellationToken">取消令牌</param>
    public static void RunEveryFrameCount(Action<int> action, int frameCount, PlayerLoopTiming timing = PlayerLoopTiming.Update, CancellationToken cancellationToken = default)
    {
        EveryFrameCount(action, frameCount, timing, cancellationToken).Forget();
    }

    /// <summary>
    /// 每隔指定秒数执行任务（循环执行直到取消）（不等待）
    /// </summary>
    /// <param name="action">要执行的操作</param>
    /// <param name="intervalSeconds">间隔时间（秒）</param>
    /// <param name="ignoreTimeScale">是否忽略时间缩放</param>
    /// <param name="cancellationToken">取消令牌</param>
    public static void RunEveryInterval(Action action, float intervalSeconds, bool ignoreTimeScale = false, CancellationToken cancellationToken = default)
    {
        EveryInterval(action, intervalSeconds, ignoreTimeScale, cancellationToken).Forget();
    }

    /// <summary>
    /// 每隔指定秒数执行任务（执行指定次数）（不等待）
    /// </summary>
    /// <param name="action">要执行的操作（参数为当前执行次数）</param>
    /// <param name="intervalSeconds">间隔时间（秒）</param>
    /// <param name="repeatCount">重复次数</param>
    /// <param name="ignoreTimeScale">是否忽略时间缩放</param>
    /// <param name="cancellationToken">取消令牌</param>
    public static void RunEveryIntervalCount(Action<int> action, float intervalSeconds, int repeatCount, bool ignoreTimeScale = false, CancellationToken cancellationToken = default)
    {
        EveryIntervalCount(action, intervalSeconds, repeatCount, ignoreTimeScale, cancellationToken).Forget();
    }

    /// <summary>
    /// 重试执行（失败后自动重试）（不等待）
    /// </summary>
    /// <param name="taskFactory">任务工厂函数</param>
    /// <param name="maxRetryCount">最大重试次数</param>
    /// <param name="retryDelaySeconds">重试间隔（秒）</param>
    /// <param name="cancellationToken">取消令牌</param>
    public static void RunRetryAsync(Func<UniTask> taskFactory, int maxRetryCount = 3, float retryDelaySeconds = 1f, CancellationToken cancellationToken = default)
    {
        RetryAsync(taskFactory, maxRetryCount, retryDelaySeconds, cancellationToken).Forget();
    }
}
}
