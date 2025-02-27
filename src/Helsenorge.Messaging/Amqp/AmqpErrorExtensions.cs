﻿/* 
 * Copyright (c) 2020-2023, Norsk Helsenett SF and contributors
 * See the file CONTRIBUTORS for details.
 * 
 * This file is licensed under the MIT license
 * available at https://raw.githubusercontent.com/helsenorge/Helsenorge.Messaging/master/LICENSE
 */

using Amqp;
using Amqp.Framing;
using Helsenorge.Messaging.Amqp.Exceptions;
using System;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Text;
using AmqpException = Amqp.AmqpException;

namespace Helsenorge.Messaging.Amqp
{
    internal static class AmqpErrorExtensions
    {
        public static Exception ToBusException(this Exception exception)
        {
            var stringBuilder = new StringBuilder();
            stringBuilder.Append(exception.Message);

            switch (exception)
            {
                case SocketException e:
                    stringBuilder.AppendFormat(CultureInfo.InvariantCulture, $" ErrorCode: {e.SocketErrorCode}");
                    return new AmqpCommunicationException(stringBuilder.ToString(), exception);

                case IOException _:
                    if (exception.InnerException is SocketException socketException)
                        stringBuilder.AppendFormat(CultureInfo.InvariantCulture, $" ErrorCode: {socketException.SocketErrorCode}");
                    return new AmqpCommunicationException(stringBuilder.ToString(), exception);

                case AmqpException amqpException:
                    return amqpException.Error.ToBusException(amqpException);

                case OperationCanceledException operationCanceledException when operationCanceledException.InnerException is AmqpException amqpException:
                    return amqpException.Error.ToBusException(operationCanceledException);

                case OperationCanceledException _:
                    return new RecoverableAmqpException(stringBuilder.ToString(), exception);

                case TimeoutException _:
                    return new AmqpTimeoutException(stringBuilder.ToString(), exception);
            }

            return exception;
        }

        public static Exception ToBusException(this Error error, Exception exception)
        {
            return error == null
                ? new UncategorizedAmqpException("Unknown error.", exception)
                : ToBusException(error.Condition, error.Description, exception);
        }

        private static Exception ToBusException(string condition, string message, Exception exception)
        {
            if (string.Equals(condition, ErrorCode.IllegalState)
                || string.Equals(condition, ErrorCode.DetachForced)
                || string.Equals(condition, ErrorCode.ConnectionForced)
                || string.Equals(condition, ErrorCode.Stolen)
                || string.Equals(condition, ErrorCode.UnattachedHandle)
                || string.Equals(condition, ErrorCode.HandleInUse)
                || string.Equals(condition, ErrorCode.ErrantLink)
                || string.Equals(condition, ErrorCode.WindowViolation)
                || string.Equals(condition, ErrorCode.ConnectionRedirect))
            {
                return new RecoverableAmqpException(message, exception);
            }

            if (string.Equals(condition, AmqpClientConstants.TimeoutError)
                || string.Equals(condition, ErrorCode.TransactionTimeout))
            {
                return new AmqpTimeoutException(message, exception);
            }

            if (string.Equals(condition, ErrorCode.NotFound))
            {
                return new MessagingEntityNotFoundException(message, exception);
            }

            if (string.Equals(condition, ErrorCode.NotImplemented))
            {
                return new NotSupportedException(message, exception);
            }

            if (string.Equals(condition, ErrorCode.NotAllowed))
            {
                return new InvalidOperationException(message, exception);
            }

            if (string.Equals(condition, ErrorCode.UnauthorizedAccess) ||
                string.Equals(condition, AmqpClientConstants.AuthorizationFailedError))
            {
                return new UnauthorizedException(message, exception);
            }

            if (string.Equals(condition, AmqpClientConstants.ServerBusyError))
            {
                return new ServerBusyException(message, exception);
            }

            if (string.Equals(condition, AmqpClientConstants.ArgumentError))
            {
                return new ArgumentException(message, exception);
            }

            if (string.Equals(condition, AmqpClientConstants.ArgumentOutOfRangeError))
            {
                return new ArgumentOutOfRangeException(message, exception);
            }

            if (string.Equals(condition, AmqpClientConstants.EntityDisabledError))
            {
                return new MessagingEntityDisabledException(message, exception);
            }

            if (string.Equals(condition, AmqpClientConstants.MessageLockLostError))
            {
                return new MessageLockLostException(message, exception);
            }

            if (string.Equals(condition, AmqpClientConstants.SessionLockLostError))
            {
                return new SessionLockLostException(message, exception);
            }

            if (string.Equals(condition, ErrorCode.ResourceLimitExceeded))
            {
                return new QuotaExceededException(message, exception);
            }

            if (string.Equals(condition, ErrorCode.MessageSizeExceeded))
            {
                return new MessageSizeExceededException(message, exception);
            }

            if(string.Equals(condition, ErrorCode.TransferLimitExceeded))
            {
                return new TransferLimitExceededException(message, exception);
            }

            if (string.Equals(condition, AmqpClientConstants.MessageNotFoundError))
            {
                return new MessageNotFoundException(message, exception);
            }

            if (string.Equals(condition, AmqpClientConstants.SessionCannotBeLockedError))
            {
                return new SessionCannotBeLockedException(message, exception);
            }

            if(string.Equals(condition, ErrorCode.FramingError))
            {
                return new FramingErrorException(message, exception);
            }

            if (string.Equals(condition, ErrorCode.FrameSizeTooSmall))
            {
                return new FrameSizeTooSmallException(message, exception);
            }

            if (string.Equals(condition, ErrorCode.ResourceDeleted))
            {
                return new ResourceDeletedException(message, exception);
            }

            if (string.Equals(condition, ErrorCode.ResourceLocked))
            {
                return new ResourceLockedException(message, exception);
            }

            if (string.Equals(condition, ErrorCode.MessageReleased))
            {
                return new MessageReleasedException(message, exception);
            }

            if (string.Equals(condition, ErrorCode.InvalidField))
            {
                return new InvalidFieldException(message, exception);
            }

            if (string.Equals(condition, ErrorCode.DecodeError))
            {
                return new DecodeErrorException(message, exception);
            }

            if (string.Equals(condition, ErrorCode.InternalError))
            {
                return new InternalErrorException(message, exception);
            }

            return new UncategorizedAmqpException(message, condition, exception);
        }
    }
}
