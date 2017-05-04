﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using JsonRpc.Standard.Contracts;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace JsonRpc.Standard.Server
{
    /// <summary>
    /// Provides options for <see cref="JsonRpcServiceHost"/>.
    /// </summary>
    [Flags]
    public enum JsonRpcServiceHostOptions
    {
        /// <summary>
        /// No options.
        /// </summary>
        None = 0,
        /// <summary>
        /// Makes the response sequence consistent with the request order.
        /// </summary>
        ConsistentResponseSequence,
    }

    internal class JsonRpcServiceHost : IJsonRpcServiceHost
    {

        internal JsonRpcServiceHost(JsonRpcServerContract contract, JsonRpcServiceHostOptions options)
        {
            if (contract == null) throw new ArgumentNullException(nameof(contract));
            Propagator = new TransformBlock<Message, ResponseMessage>(
                (Func<Message, Task<ResponseMessage>>) ReaderAction,
                new ExecutionDataflowBlockOptions
                {
                    EnsureOrdered = (options & JsonRpcServiceHostOptions.ConsistentResponseSequence) ==
                                    JsonRpcServiceHostOptions.ConsistentResponseSequence
                });
            // Drain null responses generated by RpcMethodEntryPoint.
            Propagator.LinkTo(DataflowBlock.NullTarget<ResponseMessage>(), m => m == null);
            Contract = contract;
            Options = options;
        }

        protected IPropagatorBlock<Message, ResponseMessage> Propagator { get; }

        internal ISession Session { get; set; }

        internal JsonRpcServerContract Contract { get; }

        internal JsonRpcServiceHostOptions Options { get; }

        public IServiceFactory ServiceFactory { get; set; }

        internal IJsonRpcMethodBinder MethodBinder { get; set; }

        internal IList<Func<RequestContext, Func<Task>, Task>> interceptionHandlers { get; set; }

        internal ILogger Logger { get; set; }

        /// <inheritdoc />
        public IDisposable Attach(ISourceBlock<Message> source, ITargetBlock<ResponseMessage> target)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (target == null) throw new ArgumentNullException(nameof(target));
            var d1 = source.LinkTo(Propagator, new DataflowLinkOptions {PropagateCompletion = true},
                m => m is GeneralRequestMessage);
            var d2 = Propagator.LinkTo(target, new DataflowLinkOptions {PropagateCompletion = true},
                m => m != null);
            return Utility.CombineDisposable(d1, d2);
        }

        private async Task<ResponseMessage> ReaderAction(Message message)
        {
            var request = message as GeneralRequestMessage;
            if (request == null) return null;
            var pipelineTask = InvokePipeline(request);
            if (request is RequestMessage)
            {
                // We need to wait for an response
                return await pipelineTask.ConfigureAwait(false);
            }
            // For notification, we just forget it…
            return null;
        }

        private async Task<ResponseMessage> InvokePipeline(GeneralRequestMessage request)
        {
            var ct = CancellationToken.None;
            if (ct.IsCancellationRequested) return null;
            // TODO provides a way to cancel the request from inside JsonRpcService.
            var context = new RequestContext(this, Session, request, ct);
            var pipeline = Utility.Bind(DispatchRpcMethod, context);
            if (interceptionHandlers != null)
            {
                foreach (var handler in interceptionHandlers.Reverse())
                {
                    pipeline = Utility.Bind(handler, context, pipeline);
                }
            }
            try
            {
                await pipeline().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Swallow any exceptions
                Logger.LogError(1000, ex, "Unhandled exception while processing the request.\r\n{exception}", ex);
                if (context.Response != null)
                {
                    context.Response.Result = null;
                    context.Response.Error = new ResponseError(JsonRpcErrorCode.InternalError,
                        "Unhandled exception while processing the request.");
                }
            }
            return context.Response;
        }

        private void TrySetErrorResponse(RequestContext context, JsonRpcErrorCode errorCode,
            string message)
        {
            Logger.LogError("({code}) {message}", errorCode, message);
            if (context.Response == null) return;
            context.Response.Error = new ResponseError(errorCode, message);
        }

        private bool ValidateRequest(RequestContext context)
        {
            if (context.Request.Method == null)
            {
                // Even "method": null is still allowed.
                TrySetErrorResponse(context, JsonRpcErrorCode.InvalidRequest,
                    "\"method\" property is missing in the request.");
                return false;
            }
            if (context.Request.Parameters is JValue jv)
            {
                if (jv.Type != JTokenType.Null && jv.Type != JTokenType.Undefined)
                {
                    TrySetErrorResponse(context, JsonRpcErrorCode.InvalidRequest,
                        "Invalid \"params\" value.");
                    return false;
                }
            }
            return true;
        }

        private async Task DispatchRpcMethod(RequestContext context)
        {
            if (!ValidateRequest(context)) return;
            JsonRpcMethod method;
            try
            {
                if (Contract.Methods.TryGetValue(context.Request.Method, out var candidates))
                {
                    method = MethodBinder.TryBindToMethod(candidates, context);
                }
                else
                {
                    TrySetErrorResponse(context, JsonRpcErrorCode.MethodNotFound,
                        $"Method \"{context.Request.Method}\" is not found.");
                    return;
                }
            }
            catch (AmbiguousMatchException)
            {
                TrySetErrorResponse(context, JsonRpcErrorCode.InvalidParams,
                    $"Invocation of method \"{context.Request.Method}\" is ambiguous.");
                return;
            }
            if (method == null)
            {
                TrySetErrorResponse(context, JsonRpcErrorCode.InvalidParams,
                    $"Cannot find method \"{context.Request.Method}\" with matching signature.");
                return;
            }
            // Parse the arguments
            object[] args;
            try
            {
                args = method.UnmarshalArguments(context.Request);
            }
            catch (ArgumentException ex)
            {
                // Signature not match. This is not likely to happen. Still there might be problem with binder.
                TrySetErrorResponse(context, JsonRpcErrorCode.InvalidParams, ex.Message);
                return;
            }
            catch (Exception ex)
            {
                TrySetErrorResponse(context, JsonRpcErrorCode.InvalidParams, ex.Message);
                if (context.Response != null)
                    context.Response.Error = ResponseError.FromException(ex);
                return;
            }
            // Call the method
            try
            {
                var result = await method.Invoker.InvokeAsync(context, args).ConfigureAwait(false);
                // Produce the response.
                if (context.Response != null && context.Response.Result == null && context.Response.Error == null)
                {
                    context.Response.Result = method.ReturnParameter.Converter.ValueToJson(result);
                    Debug.Assert(context.Response.Result != null);
                }
            }
            catch (TargetInvocationException ex)
            {
                if (context.Response != null)
                    context.Response.Error = ResponseError.FromException(ex.InnerException);
                return;
            }
            catch (Exception ex)
            {
                if (context.Response != null)
                    context.Response.Error = ResponseError.FromException(ex);
                return;
            }
        }
    }
}
