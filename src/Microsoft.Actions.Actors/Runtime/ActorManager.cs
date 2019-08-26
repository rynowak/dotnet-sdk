﻿// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

namespace Microsoft.Actions.Actors.Runtime
{    
    using System;
    using System.Collections.Concurrent;
    using System.IO;
    using System.Reflection;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Actions.Actors;
    using Microsoft.Actions.Actors.Builder;
    using Microsoft.Actions.Actors.Communication;
    using Newtonsoft.Json;

    /// <summary>
    /// Manages Actors of a specific actor type.
    /// </summary>
    internal sealed class ActorManager : IActorManager
    {
        private const string TraceType = "ActorManager";
        private const string ReceiveReminderMethodName = "ReceiveReminderAsync";
        private const string TimerMethodName = "FireTimerAsync";
        private readonly ActorService actorService;
        private readonly ConcurrentDictionary<ActorId, Actor> activeActors;
        private readonly ActorMethodContext reminderMethodContext;
        private readonly ActorMethodContext timerMethodContext;
        private readonly ActorMessageSerializersManager serializersManager;
        private IActorMessageBodyFactory messageBodyFactory;

        // method dispatchermap used by remoting calls.
        private ActorMethodDispatcherMap methodDispatcherMap;

        // method info map used by non-remoting calls.
        private ActorMethodInfoMap actorMethodInfoMap;

        internal ActorManager(ActorService actorService)
        {
            this.actorService = actorService;

            // map for remoting calls.
            this.methodDispatcherMap = new ActorMethodDispatcherMap(this.actorService.ActorTypeInfo.InterfaceTypes);

            // map for non-remoting calls.
            this.actorMethodInfoMap = new ActorMethodInfoMap(this.actorService.ActorTypeInfo.InterfaceTypes);
            this.activeActors = new ConcurrentDictionary<ActorId, Actor>();
            this.reminderMethodContext = ActorMethodContext.CreateForReminder(ReceiveReminderMethodName);
            this.timerMethodContext = ActorMethodContext.CreateForReminder(TimerMethodName);
            this.serializersManager = IntializeSerializationManager(null);
            this.messageBodyFactory = new WrappedRequestMessageFactory();
        }

        internal ActorTypeInformation ActorTypeInfo => this.actorService.ActorTypeInfo;

        internal Task<Tuple<string, string>> DispatchWithRemotingAsync(ActorId actorId, string actorMethodName, string actionsActorheader, Stream data, CancellationToken cancellationToken)
        {
            var actorMethodContext = ActorMethodContext.CreateForActor(actorMethodName);
            
            // Get the serialized header
            var actorMessageHeader = this.serializersManager.GetHeaderSerializer()
                .DeserializeRequestHeaders(new MemoryStream(Encoding.ASCII.GetBytes(actionsActorheader)));

            var interfaceId = actorMessageHeader.InterfaceId;

            // Get the deserialized Body.
            var msgBodySerializer = this.serializersManager.GetRequestMessageBodySerializer(actorMessageHeader.InterfaceId);
            var actorMessageBody = msgBodySerializer.Deserialize(data);

            // Call the method on the method dispatcher using the Func below.
            var methodDispatcher = this.methodDispatcherMap.GetDispatcher(actorMessageHeader.InterfaceId, actorMessageHeader.MethodId);

            // Create a Func to be invoked by common method.
            async Task<Tuple<string, string>> RequestFunc(Actor actor, CancellationToken ct)
            {
                IActorResponseMessageBody responseMsgBody = null;
                var actorResponseMessageHeader = new ActorResponseMessageHeader();

                try
                {
                    responseMsgBody = (IActorResponseMessageBody)await methodDispatcher.DispatchAsync(
                        actor,
                        actorMessageHeader.MethodId,
                        actorMessageBody,
                        this.messageBodyFactory,
                        ct);
                }
                catch (Exception exception)
                {
                    // set response header for error
                    // TODO come up with error messages translation layer
                    actorResponseMessageHeader.AddHeader(Constants.ErrorResponseHeaderName, Encoding.ASCII.GetBytes(exception.Message));
                }

                var responseMessage = this.CreateResponseMessage(actorResponseMessageHeader, responseMsgBody, interfaceId);

                return responseMessage;
            }

            return this.DispatchInternalAsync<Tuple<string, string>>(actorId, actorMethodContext, RequestFunc, cancellationToken);
        }

        internal async Task DispatchWithoutRemotingAsync(ActorId actorId, string actorMethodName, Stream requestBodyStream, Stream responseBodyStream, CancellationToken cancellationToken)
        {
            var actorMethodContext = ActorMethodContext.CreateForActor(actorMethodName);
            var serializer = new JsonSerializer();

            // Create a Func to be invoked by common method.
            var methodInfo = this.actorMethodInfoMap.LookupActorMethodInfo(actorMethodName);

            async Task<object> RequestFunc(Actor actor, CancellationToken ct)
            {
                var parameters = methodInfo.GetParameters();
                dynamic awaitable;

                if (parameters.Length == 0)
                {
                    awaitable = methodInfo.Invoke(actor, null);
                }
                else
                {
                    // deserialize using stream.
                    var type = parameters[0].ParameterType;
                    var deserializedType = default(object);
                    using (var streamReader = new StreamReader(requestBodyStream))
                    {
                        using (var reader = new JsonTextReader(streamReader))
                        {
                            deserializedType = serializer.Deserialize(reader, type);
                        }
                    }

                    awaitable = methodInfo.Invoke(actor, new object[] { deserializedType });
                }

                await awaitable;

                // Write Response back if method's return type is other than Task.
                // Serialize result if it has result (return type was not just Task.)
                if (methodInfo.ReturnType.Name != typeof(Task).Name)
                {
                    // already await, Getting result will be non blocking.
                    var x = awaitable.GetAwaiter().GetResult();
                    return x;
                }
                else
                {
                    return default(object);
                }
            }

            var result = await this.DispatchInternalAsync(actorId, actorMethodContext, RequestFunc, cancellationToken);

            using (var streamWriter = new StreamWriter(responseBodyStream))
            {
                using (var writer = new JsonTextWriter(streamWriter))
                {
                    serializer.Serialize(writer, result);
                }
            }
        }

        internal Task FireReminderAsync(ActorId actorId, string reminderName, Stream requestBodyStream, CancellationToken cancellationToken = default(CancellationToken))
        {
            // Only FireReminder if its IRemindable, else ignore it.
            if (this.ActorTypeInfo.IsRemindable)
            {
                var reminderdata = ReminderInfo.Deserialize(requestBodyStream);

                // Create a Func to be invoked by common method.
                async Task<byte[]> RequestFunc(Actor actor, CancellationToken ct)
                {
                    await
                        (actor as IRemindable).ReceiveReminderAsync(
                            reminderName,
                            reminderdata.Data,
                            reminderdata.DueTime,
                            reminderdata.Period);

                    return null;
                }

                return this.DispatchInternalAsync(actorId, this.reminderMethodContext, RequestFunc, cancellationToken);
            }

            return Task.CompletedTask;
        }

        internal Task FireTimerAsync(ActorId actorId, string timerName, CancellationToken cancellationToken = default(CancellationToken))
        {
            // Create a Func to be invoked by common method.
            async Task<byte[]> RequestFunc(Actor actor, CancellationToken ct)
            {
                await
                    actor.FireTimerAsync(timerName);

                return null;
            }

            return this.DispatchInternalAsync(actorId, this.timerMethodContext, RequestFunc, cancellationToken);
        }

        internal async Task ActivateActor(ActorId actorId)
        {
            // An actor is activated by "actions" runtime when a call is to be made for an actor.
            var actor = this.actorService.CreateActor(actorId);
            await actor.OnActivateInternalAsync();

            // Add actor to activeActors only after OnActivate succeeds (user code can throw error from its override of Activate method.)
            // Always add the new instance.
            this.activeActors.AddOrUpdate(actorId, actor, (key, oldValue) => actor);
        }

        internal async Task DeactivateActor(ActorId actorId)
        {
            if (this.activeActors.TryRemove(actorId, out var deactivatedActor))
            {
                await deactivatedActor.OnDeactivateInternalAsync();
            }
        }

        private static ActorMessageSerializersManager IntializeSerializationManager(
            IActorMessageBodySerializationProvider serializationProvider)
        {
            // TODO serializer settings 
            return new ActorMessageSerializersManager(
                serializationProvider,
                new ActorMessageHeaderSerializer());
        }

        private async Task<T> DispatchInternalAsync<T>(ActorId actorId, ActorMethodContext actorMethodContext, Func<Actor, CancellationToken, Task<T>> actorFunc, CancellationToken cancellationToken)
        {
            if (!this.activeActors.TryGetValue(actorId, out var actor))
            {
                // This should never happen, as "Actions" runtime activates the actor first. if it ever it would mean a bug in "Actions" runtime.
                var errorMsg = $"Actor {actorId} is not yet activated.";
                ActorTrace.Instance.WriteError(TraceType, errorMsg);
                throw new InvalidOperationException(errorMsg);
            }

            var retval = default(T);

            try
            {
                // invoke the function of the actor
                await actor.OnPreActorMethodAsyncInternal(actorMethodContext);
                retval = await actorFunc.Invoke(actor, cancellationToken);
                await actor.OnPostActorMethodAsyncInternal(actorMethodContext);
            }
            catch
            {
                actor.OnInvokeFailed();
                throw;
            }

            return retval;
        }

        private Tuple<string, string> CreateResponseMessage(IActorResponseMessageHeader header, IActorResponseMessageBody msgBody, int interfaceId)
        {
            string responseHeader = string.Empty;
            if (header != null)
            {
                var responseHeaderBytes = this.serializersManager.GetHeaderSerializer().SerializeResponseHeader(header);

                if (responseHeaderBytes != null)
                {
                    responseHeader = Encoding.UTF8.GetString(responseHeaderBytes, 0, responseHeaderBytes.Length);
                }
            }

            string responseMsgBody = string.Empty;
            if (msgBody != null)
            {
                var responseSerializer = this.serializersManager.GetResponseMessageBodySerializer(interfaceId);

                var responseMsgBodyBytes = responseSerializer.Serialize(msgBody);
                responseMsgBody = Encoding.UTF8.GetString(responseMsgBodyBytes, 0, responseMsgBodyBytes.Length);
            }

            return new Tuple<string, string>(responseHeader, responseMsgBody);
        }
    }
}
