using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;

namespace MBW.Tools.GhStandardContent.Helpers;

internal static class ParallelQueue
{
    public static async Task RunParallel<TInput>(
        Func<TInput, CancellationToken, Task> operation, IEnumerable<TInput> data,
        CancellationToken token = default)
    {
        _ = await RunParallel(async (input, cancellationToken) =>
        {
            await operation(input, cancellationToken);

            return (object)null;
        }, data, token);
    }

    public static async Task<IList<TResult>> RunParallel<TInput, TResult>(
        Func<TInput, CancellationToken, Task<TResult>> operation, IEnumerable<TInput> data,
        CancellationToken token = default)
    {
        using CancellationTokenSource localCancel = CancellationTokenSource.CreateLinkedTokenSource(token);

        List<TResult> results = new List<TResult>();
        ActionBlock<TInput> action = new ActionBlock<TInput>(async input =>
        {
            TResult res;
            try
            {
                res = await operation(input, localCancel.Token);
            }
            catch (Exception)
            {
                localCancel.Cancel();
                throw;
            }

            lock (results)
                results.Add(res);
        }, new ExecutionDataflowBlockOptions
        {
            BoundedCapacity = 4,
            CancellationToken = localCancel.Token,
            MaxDegreeOfParallelism = 3,
            SingleProducerConstrained = true
        });

        foreach (TInput input in data)
            await action.SendAsync(input, localCancel.Token);

        action.Complete();

        await action.Completion;

        return results;
    }
}