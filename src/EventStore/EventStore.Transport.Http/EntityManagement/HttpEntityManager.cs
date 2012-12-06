// Copyright (c) 2012, Event Store LLP
// All rights reserved.
// 
// Redistribution and use in source and binary forms, with or without
// modification, are permitted provided that the following conditions are
// met:
// 
// Redistributions of source code must retain the above copyright notice,
// this list of conditions and the following disclaimer.
// Redistributions in binary form must reproduce the above copyright
// notice, this list of conditions and the following disclaimer in the
// documentation and/or other materials provided with the distribution.
// Neither the name of the Event Store LLP nor the names of its
// contributors may be used to endorse or promote products derived from
// this software without specific prior written permission
// THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
// "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
// LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
// A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT
// HOLDER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
// SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT
// LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
// DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY
// THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
// (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
// OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
// 
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using EventStore.Common.Log;
using EventStore.Common.Utils;

namespace EventStore.Transport.Http.EntityManagement
{
    public sealed class HttpEntityManager
    {
        private static readonly ILogger Log = LogManager.GetLoggerFor<HttpEntityManager>();

        public object AsyncState { get; set; }
        public readonly HttpEntity HttpEntity;

        private int _processing;
        private readonly string[] _allowedMethods;
        private readonly Action<HttpEntity> _onRequestSatisfied;

        internal HttpEntityManager(HttpEntity httpEntity, string[] allowedMethods, Action<HttpEntity> onRequestSatisfied)
        {
            Ensure.NotNull(httpEntity, "httpEntity");
            Ensure.NotNull(allowedMethods, "allowedMethods");
            Ensure.NotNull(onRequestSatisfied, "onRequestSatisfied");

            HttpEntity = httpEntity;

            _allowedMethods = allowedMethods;
            _onRequestSatisfied = onRequestSatisfied;
        }

        private void SetResponseCode(int code)
        {
            try
            {
                HttpEntity.Response.StatusCode = code;
            }
            catch (ProtocolViolationException e)
            {
                Log.InfoException(e, "Attempt to set invalid http status code occurred");
            }
            catch (ObjectDisposedException e)
            {
                Log.InfoException(e, "Attempt to set http status code on disponsed response object, ignoring...");
            }
        }

        private void SetResponseDescription(string desc)
        {
            try
            {
                HttpEntity.Response.StatusDescription = desc;
            }
            catch (ObjectDisposedException e)
            {
                Log.InfoException(e, "Attempt to set http status description on disponsed response object, ignoring...");
            }
            catch (ArgumentException e)
            {
                Log.InfoException(e, "Description string '{0}' did not pass validation. Status description did not set",
                                  desc);
            }
        }

        private void SetContentType(string contentType)
        {
            try
            {
                HttpEntity.Response.ContentType = contentType;
            }
            catch (ObjectDisposedException e)
            {
                Log.InfoException(e, "Attempt to set response content type on disponsed response object, ignoring...");
            }
            catch (InvalidOperationException e)
            {
                Log.InfoException(e, "Attempt to set response content type resulted in IOE");
            }
            catch (ArgumentOutOfRangeException e)
            {
                Log.InfoException(e, "Invalid response type");
            }
        }

        private void SetResponseLength(long length)
        {
            try
            {
                HttpEntity.Response.ContentLength64 = length;
            }
            catch (InvalidOperationException e)
            {
                Log.InfoException(e, "Content length did not set");
            }
            catch (ArgumentOutOfRangeException e)
            {
                Log.InfoException(e, "Attempt to set invalid value ('{0}') as content length", length);
            }
        }

        private void SetRequiredHeaders()
        {
            try
            {
                HttpEntity.Response.AddHeader("Access-Control-Allow-Methods", string.Join(", ", _allowedMethods));
                HttpEntity.Response.AddHeader("Access-Control-Allow-Headers", "Content-Type, X-Requested-With, X-PINGOTHER");
                HttpEntity.Response.AddHeader("Access-Control-Allow-Origin", "*");
            }
            catch (Exception e)
            {
                Log.InfoException(e, "Failed to set required response headers");
            }
        }

        private void SetAdditionalHeaders(IEnumerable<KeyValuePair<string, string>> headers)
        {
            try
            {
                foreach (var kvp in headers)
                {
                    HttpEntity.Response.AddHeader(kvp.Key, kvp.Value);
                }
            }
            catch (Exception e)
            {
                Log.InfoException(e, "Failed to set additional response headers");
            }
        }

        public void ReadRequestAsync(Action<HttpEntityManager, byte[]> onReadSuccess, Action<Exception> onError)
        {
            Ensure.NotNull(onReadSuccess, "OnReadSuccess");
            Ensure.NotNull(onError, "onError");

            var state = new ManagerOperationState(HttpEntity, onReadSuccess, onError)
                {
                    InputStream = HttpEntity.Request.InputStream,
                    OutputStream = new MemoryStream()
                };

            var copier = new AsyncStreamCopier<ManagerOperationState>(state.InputStream, state.OutputStream, state);
            copier.Completed += RequestRead;
            copier.Start();
        }

        public bool BeginReply(int code, string description, string contentType, KeyValuePair<string, string>[] headers)
        {
            bool isAlreadyProcessing = Interlocked.CompareExchange(ref _processing, 1, 0) == 1;
            if (isAlreadyProcessing)
                return false;

            SetResponseCode(code);
            SetResponseDescription(description);
            SetContentType(contentType);
            SetRequiredHeaders();
            SetAdditionalHeaders(headers ?? Enumerable.Empty<KeyValuePair<string, string>>());
            return true;
        }

        public void ContinueReply(byte[] response, Action<Exception> onError, Action completed)
        {
            Ensure.NotNull(onError, "onError");
            Ensure.NotNull(completed, "completed");

            ContinueWriteResponseAsync(response, onError, (sender, args) =>
                {
                    ResponsePartWritten(sender);  
                    completed(); 
                });
        }

        public void EndReply(Action<Exception> onError)
        {
            IOStreams.SafelyDispose(HttpEntity.Response.OutputStream);
            CloseConnection(e => Log.ErrorException(e, "Close connection error (after successful response write)"));
        }

        public void Reply(byte[] response,
                          int code,
                          string description,
                          string contentType,
                          KeyValuePair<string, string>[] headers,
                          Action<Exception> onError)
        {
            Ensure.NotNull(onError, "onError");

            if (!BeginReply(code, description, contentType, headers)) return;

            if (response == null || response.Length == 0)
            {
                SetResponseLength(0);
                CloseConnection(onError);
            }
            else
            {
                SetResponseLength(response.Length);
                ContinueWriteResponseAsync(response, onError, ResponseWritten);
            }
        }

        private void ContinueWriteResponseAsync(byte[] response, Action<Exception> onError, EventHandler copierOnCompleted)
        {
            var state = new ManagerOperationState(HttpEntity, (sender, e) => { }, onError)
                {
                    InputStream = new MemoryStream(response),
                    OutputStream = HttpEntity.Response.OutputStream
                };
            var copier = new AsyncStreamCopier<ManagerOperationState>(state.InputStream, state.OutputStream, state);
            copier.Completed += copierOnCompleted;
            copier.Start();
        }

        private void ResponseWritten(object sender, EventArgs eventArgs)
        {
            var copier = (AsyncStreamCopier<ManagerOperationState>) sender;
            ManagerOperationState state = copier.AsyncState;

            if (copier.Error != null)
            {
                state.DisposeIOStreams();
                CloseConnection(e => Log.ErrorException(e, "Close connection error (after crash in write response)"));

                state.OnError(copier.Error);
                return;
            }

            state.DisposeIOStreams();
            CloseConnection(e => Log.ErrorException(e, "Close connection error (after successful response write)"));
        }

        private void ResponsePartWritten(object sender)
        {
            var copier = (AsyncStreamCopier<ManagerOperationState>)sender;
            var state = copier.AsyncState;

            if (copier.Error != null)
            {
                state.DisposeIOStreams();
                CloseConnection(e => Log.ErrorException(e, "Close connection error (after crash in write response)"));

                state.OnError(copier.Error);
            }
        }

        private void RequestRead(object sender, EventArgs e)
        {
            var copier = (AsyncStreamCopier<ManagerOperationState>) sender;
            var state = copier.AsyncState;

            if (copier.Error != null)
            {
                state.DisposeIOStreams();
                CloseConnection(exc => Log.ErrorException(exc, "Close connection error (after crash in read request)"));

                state.OnError(copier.Error);
                return;
            }

            state.OutputStream.Seek(0, SeekOrigin.Begin);
            var memory = (MemoryStream)state.OutputStream;

            var request = memory.GetBuffer();
            if (memory.Length != memory.GetBuffer().Length)
            {
                request = new byte[memory.Length];
                Buffer.BlockCopy(memory.GetBuffer(), 0, request, 0, (int)memory.Length);
            }
            state.OnReadSuccess(this, request);
        }

        private void CloseConnection(Action<Exception> onError)
        {
            try
            {
                _onRequestSatisfied(HttpEntity);
                HttpEntity.Response.Close();
            }
            catch (Exception e)
            {
                onError(e);
            }
        }
    }
}