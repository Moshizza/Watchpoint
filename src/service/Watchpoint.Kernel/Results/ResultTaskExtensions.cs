namespace Watchpoint.Kernel.Results;

/// <summary>
/// Asynchronous versions of <see cref="ResultExtensions"/> on <see cref="Task{TResult}"/> and
/// <see cref="ValueTask{TResult}"/> of <see cref="Result{T}"/>, plus helpers taking asynchronous functions.
/// </summary>
public static class ResultTaskExtensions
{
    /// <summary>Awaits the result, then <see cref="ResultExtensions.Map{T, TOut}(Result{T}, Func{T, TOut})"/>.</summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <typeparam name="TOut">The new value type.</typeparam>
    /// <param name="task">The pending result.</param>
    /// <param name="map">The transformation.</param>
    /// <returns>The transformed result.</returns>
    public static Task<Result<TOut>> Map<T, TOut>(this Task<Result<T>> task, Func<T, TOut> map) =>
        ThenAsync(Required(task), r => r.Map(map));

    /// <summary>Awaits the result, then <see cref="ResultExtensions.Bind{T, TOut}(Result{T}, Func{T, Result{TOut}})"/>.</summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <typeparam name="TOut">The new value type.</typeparam>
    /// <param name="task">The pending result.</param>
    /// <param name="bind">The next operation; not called on failure.</param>
    /// <returns>The chained result.</returns>
    public static Task<Result<TOut>> Bind<T, TOut>(this Task<Result<T>> task, Func<T, Result<TOut>> bind) =>
        ThenAsync(Required(task), r => r.Bind(bind));

    /// <summary>Awaits the result, then chains an asynchronous operation.</summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <typeparam name="TOut">The new value type.</typeparam>
    /// <param name="task">The pending result.</param>
    /// <param name="bind">The next operation; not called on failure.</param>
    /// <returns>The chained result.</returns>
    public static async Task<Result<TOut>> BindAsync<T, TOut>(this Task<Result<T>> task, Func<T, Task<Result<TOut>>> bind) =>
        await (await Required(task)).BindAsync(bind);

    /// <summary>Chains an asynchronous operation on a success.</summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <typeparam name="TOut">The new value type.</typeparam>
    /// <param name="result">The result.</param>
    /// <param name="bind">The next operation; not called on failure.</param>
    /// <returns>The chained result.</returns>
    public static async Task<Result<TOut>> BindAsync<T, TOut>(this Result<T> result, Func<T, Task<Result<TOut>>> bind)
    {
        ArgumentNullException.ThrowIfNull(bind);
        return result.TryGetValue(out var value, out var error) ? await bind(value) : error;
    }

    /// <summary>Awaits the result, then <see cref="ResultExtensions.Ensure{T}"/>.</summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="task">The pending result.</param>
    /// <param name="predicate">The condition on the value.</param>
    /// <param name="error">The error when <paramref name="predicate"/> is false.</param>
    /// <returns>The result, or <paramref name="error"/>.</returns>
    public static Task<Result<T>> Ensure<T>(this Task<Result<T>> task, Func<T, bool> predicate, Error error) =>
        ThenAsync(Required(task), r => r.Ensure(predicate, error));

    /// <summary>Awaits the result, then <see cref="ResultExtensions.Match{T, TOut}(Result{T}, Func{T, TOut}, Func{Error, TOut})"/>.</summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <typeparam name="TOut">The output type.</typeparam>
    /// <param name="task">The pending result.</param>
    /// <param name="onSuccess">Called with the value on success.</param>
    /// <param name="onFailure">Called with the error on failure.</param>
    /// <returns>The output of the called function.</returns>
    public static Task<TOut> Match<T, TOut>(this Task<Result<T>> task, Func<T, TOut> onSuccess, Func<Error, TOut> onFailure) =>
        ThenAsync(Required(task), r => r.Match(onSuccess, onFailure));

    /// <summary>Awaits the result, then <see cref="ResultExtensions.Tap{T}(Result{T}, Action{T})"/>.</summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="task">The pending result.</param>
    /// <param name="action">The side effect.</param>
    /// <returns>The same result.</returns>
    public static Task<Result<T>> Tap<T>(this Task<Result<T>> task, Action<T> action) =>
        ThenAsync(Required(task), r => r.Tap(action));

    /// <summary>Awaits the result, then <see cref="ResultExtensions.MapError{T}(Result{T}, Func{Error, Error})"/>.</summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="task">The pending result.</param>
    /// <param name="map">The transformation.</param>
    /// <returns>The transformed result.</returns>
    public static Task<Result<T>> MapError<T>(this Task<Result<T>> task, Func<Error, Error> map) =>
        ThenAsync(Required(task), r => r.MapError(map));

    /// <summary>Awaits the result, then <see cref="ResultExtensions.Map{T, TOut}(Result{T}, Func{T, TOut})"/>.</summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <typeparam name="TOut">The new value type.</typeparam>
    /// <param name="task">The pending result.</param>
    /// <param name="map">The transformation.</param>
    /// <returns>The transformed result.</returns>
    public static ValueTask<Result<TOut>> Map<T, TOut>(this ValueTask<Result<T>> task, Func<T, TOut> map) =>
        ThenAsync(task, r => r.Map(map));

    /// <summary>Awaits the result, then <see cref="ResultExtensions.Bind{T, TOut}(Result{T}, Func{T, Result{TOut}})"/>.</summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <typeparam name="TOut">The new value type.</typeparam>
    /// <param name="task">The pending result.</param>
    /// <param name="bind">The next operation; not called on failure.</param>
    /// <returns>The chained result.</returns>
    public static ValueTask<Result<TOut>> Bind<T, TOut>(this ValueTask<Result<T>> task, Func<T, Result<TOut>> bind) =>
        ThenAsync(task, r => r.Bind(bind));

    /// <summary>Awaits the result, then chains an asynchronous operation.</summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <typeparam name="TOut">The new value type.</typeparam>
    /// <param name="task">The pending result.</param>
    /// <param name="bind">The next operation; not called on failure.</param>
    /// <returns>The chained result.</returns>
    public static async ValueTask<Result<TOut>> BindAsync<T, TOut>(this ValueTask<Result<T>> task, Func<T, ValueTask<Result<TOut>>> bind)
    {
        ArgumentNullException.ThrowIfNull(bind);
        return (await task).TryGetValue(out var value, out var error) ? await bind(value) : error;
    }

    /// <summary>Awaits the result, then <see cref="ResultExtensions.Ensure{T}"/>.</summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="task">The pending result.</param>
    /// <param name="predicate">The condition on the value.</param>
    /// <param name="error">The error when <paramref name="predicate"/> is false.</param>
    /// <returns>The result, or <paramref name="error"/>.</returns>
    public static ValueTask<Result<T>> Ensure<T>(this ValueTask<Result<T>> task, Func<T, bool> predicate, Error error) =>
        ThenAsync(task, r => r.Ensure(predicate, error));

    /// <summary>Awaits the result, then <see cref="ResultExtensions.Match{T, TOut}(Result{T}, Func{T, TOut}, Func{Error, TOut})"/>.</summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <typeparam name="TOut">The output type.</typeparam>
    /// <param name="task">The pending result.</param>
    /// <param name="onSuccess">Called with the value on success.</param>
    /// <param name="onFailure">Called with the error on failure.</param>
    /// <returns>The output of the called function.</returns>
    public static ValueTask<TOut> Match<T, TOut>(this ValueTask<Result<T>> task, Func<T, TOut> onSuccess, Func<Error, TOut> onFailure) =>
        ThenAsync(task, r => r.Match(onSuccess, onFailure));

    /// <summary>Awaits the result, then <see cref="ResultExtensions.Tap{T}(Result{T}, Action{T})"/>.</summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="task">The pending result.</param>
    /// <param name="action">The side effect.</param>
    /// <returns>The same result.</returns>
    public static ValueTask<Result<T>> Tap<T>(this ValueTask<Result<T>> task, Action<T> action) =>
        ThenAsync(task, r => r.Tap(action));

    /// <summary>Awaits the result, then <see cref="ResultExtensions.MapError{T}(Result{T}, Func{Error, Error})"/>.</summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="task">The pending result.</param>
    /// <param name="map">The transformation.</param>
    /// <returns>The transformed result.</returns>
    public static ValueTask<Result<T>> MapError<T>(this ValueTask<Result<T>> task, Func<Error, Error> map) =>
        ThenAsync(task, r => r.MapError(map));

    private static async Task<TOut> ThenAsync<T, TOut>(Task<Result<T>> task, Func<Result<T>, TOut> then) => then(await task);

    private static async ValueTask<TOut> ThenAsync<T, TOut>(ValueTask<Result<T>> task, Func<Result<T>, TOut> then) =>
        then(await task);

    private static Task<Result<T>> Required<T>(Task<Result<T>> task) => task ?? throw new ArgumentNullException(nameof(task));
}
