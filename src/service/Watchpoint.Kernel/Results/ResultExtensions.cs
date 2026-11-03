namespace Watchpoint.Kernel.Results;

/// <summary>
/// Composition helpers for <see cref="Result"/> and <see cref="Result{T}"/>: on failure, every helper passes the
/// same <see cref="Error"/> instance through without calling the given functions.
/// </summary>
public static class ResultExtensions
{
    /// <summary>Transforms the value of a success.</summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <typeparam name="TOut">The new value type.</typeparam>
    /// <param name="result">The result.</param>
    /// <param name="map">The transformation.</param>
    /// <returns>The transformed result.</returns>
    public static Result<TOut> Map<T, TOut>(this Result<T> result, Func<T, TOut> map)
    {
        ArgumentNullException.ThrowIfNull(map);
        return result.TryGetValue(out var value, out var error) ? map(value) : error;
    }

    /// <summary>Produces a value after a success.</summary>
    /// <typeparam name="TOut">The value type.</typeparam>
    /// <param name="result">The result.</param>
    /// <param name="map">The value factory.</param>
    /// <returns>The new result.</returns>
    public static Result<TOut> Map<TOut>(this Result result, Func<TOut> map)
    {
        ArgumentNullException.ThrowIfNull(map);
        return result.IsSuccess ? map() : result.Error;
    }

    /// <summary>Chains an operation that can fail.</summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <typeparam name="TOut">The new value type.</typeparam>
    /// <param name="result">The result.</param>
    /// <param name="bind">The next operation; not called on failure.</param>
    /// <returns>The result of <paramref name="bind"/>, or the original failure.</returns>
    public static Result<TOut> Bind<T, TOut>(this Result<T> result, Func<T, Result<TOut>> bind)
    {
        ArgumentNullException.ThrowIfNull(bind);
        return result.TryGetValue(out var value, out var error) ? bind(value) : error;
    }

    /// <summary>Chains an operation without value that can fail.</summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="result">The result.</param>
    /// <param name="bind">The next operation; not called on failure.</param>
    /// <returns>The result of <paramref name="bind"/>, or the original failure.</returns>
    public static Result Bind<T>(this Result<T> result, Func<T, Result> bind)
    {
        ArgumentNullException.ThrowIfNull(bind);
        return result.TryGetValue(out var value, out var error) ? bind(value) : error;
    }

    /// <summary>Chains an operation that can fail.</summary>
    /// <param name="result">The result.</param>
    /// <param name="bind">The next operation; not called on failure.</param>
    /// <returns>The result of <paramref name="bind"/>, or the original failure.</returns>
    public static Result Bind(this Result result, Func<Result> bind)
    {
        ArgumentNullException.ThrowIfNull(bind);
        return result.IsSuccess ? bind() : result;
    }

    /// <summary>Chains an operation that can fail and returns a value.</summary>
    /// <typeparam name="TOut">The value type.</typeparam>
    /// <param name="result">The result.</param>
    /// <param name="bind">The next operation; not called on failure.</param>
    /// <returns>The result of <paramref name="bind"/>, or the original failure.</returns>
    public static Result<TOut> Bind<TOut>(this Result result, Func<Result<TOut>> bind)
    {
        ArgumentNullException.ThrowIfNull(bind);
        return result.IsSuccess ? bind() : result.Error;
    }

    /// <summary>Turns a success into a failure when a condition does not hold.</summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="result">The result.</param>
    /// <param name="predicate">The condition on the value.</param>
    /// <param name="error">The error when <paramref name="predicate"/> is false.</param>
    /// <returns>The result, or <paramref name="error"/>.</returns>
    public static Result<T> Ensure<T>(this Result<T> result, Func<T, bool> predicate, Error error)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        ArgumentNullException.ThrowIfNull(error);
        if (!result.TryGetValue(out var value, out var existing))
        {
            return existing;
        }

        return predicate(value) ? result : error;
    }

    /// <summary>Reduces a result to one value.</summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <typeparam name="TOut">The output type.</typeparam>
    /// <param name="result">The result.</param>
    /// <param name="onSuccess">Called with the value on success.</param>
    /// <param name="onFailure">Called with the error on failure.</param>
    /// <returns>The output of the called function.</returns>
    public static TOut Match<T, TOut>(this Result<T> result, Func<T, TOut> onSuccess, Func<Error, TOut> onFailure)
    {
        ArgumentNullException.ThrowIfNull(onSuccess);
        ArgumentNullException.ThrowIfNull(onFailure);
        return result.TryGetValue(out var value, out var error) ? onSuccess(value) : onFailure(error);
    }

    /// <summary>Reduces a result to one value.</summary>
    /// <typeparam name="TOut">The output type.</typeparam>
    /// <param name="result">The result.</param>
    /// <param name="onSuccess">Called on success.</param>
    /// <param name="onFailure">Called with the error on failure.</param>
    /// <returns>The output of the called function.</returns>
    public static TOut Match<TOut>(this Result result, Func<TOut> onSuccess, Func<Error, TOut> onFailure)
    {
        ArgumentNullException.ThrowIfNull(onSuccess);
        ArgumentNullException.ThrowIfNull(onFailure);
        return result.IsSuccess ? onSuccess() : onFailure(result.Error);
    }

    /// <summary>Runs a side effect on success.</summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="result">The result.</param>
    /// <param name="action">The side effect.</param>
    /// <returns>The same result.</returns>
    public static Result<T> Tap<T>(this Result<T> result, Action<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (result.IsSuccess)
        {
            action(result.Value);
        }

        return result;
    }

    /// <summary>Runs a side effect on success.</summary>
    /// <param name="result">The result.</param>
    /// <param name="action">The side effect.</param>
    /// <returns>The same result.</returns>
    public static Result Tap(this Result result, Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (result.IsSuccess)
        {
            action();
        }

        return result;
    }

    /// <summary>Transforms the error of a failure.</summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="result">The result.</param>
    /// <param name="map">The transformation.</param>
    /// <returns>The transformed result.</returns>
    public static Result<T> MapError<T>(this Result<T> result, Func<Error, Error> map)
    {
        ArgumentNullException.ThrowIfNull(map);
        return result.TryGetValue(out _, out var error) ? result : map(error);
    }

    /// <summary>Transforms the error of a failure.</summary>
    /// <param name="result">The result.</param>
    /// <param name="map">The transformation.</param>
    /// <returns>The transformed result.</returns>
    public static Result MapError(this Result result, Func<Error, Error> map)
    {
        ArgumentNullException.ThrowIfNull(map);
        return result.IsSuccess ? result : map(result.Error);
    }

    /// <summary>
    /// Combines results: success when all succeed. When any result failed with a non-validation error, the first
    /// such error wins. Otherwise a single validation failure is returned unchanged, and several are merged into
    /// one <c>common.validation.failed</c> error whose field errors are concatenated in order (a validation error
    /// without field errors contributes one field error with an empty path, its code and its arguments).
    /// </summary>
    /// <param name="results">The results.</param>
    /// <returns>The combined result.</returns>
    public static Result Combine(params Result[] results)
    {
        ArgumentNullException.ThrowIfNull(results);
        List<Error>? validation = null;
        foreach (var result in results)
        {
            if (result.IsSuccess)
            {
                continue;
            }

            var error = result.Error;
            if (error.Kind != ErrorKind.Validation)
            {
                return error;
            }

            (validation ??= []).Add(error);
        }

        if (validation is null)
        {
            return Result.Success();
        }

        if (validation.Count == 1)
        {
            return validation[0];
        }

        var fields = new List<FieldError>();
        foreach (var error in validation)
        {
            if (error.FieldErrors.Count > 0)
            {
                fields.AddRange(error.FieldErrors);
            }
            else
            {
                fields.Add(new FieldError(string.Empty, error.Code, error.Args));
            }
        }

        return CommonErrors.ValidationFailed(fields);
    }
}
