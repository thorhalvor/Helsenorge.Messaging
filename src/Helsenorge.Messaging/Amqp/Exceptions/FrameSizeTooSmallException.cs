﻿/* 
 * Copyright (c) 2020-2023, Norsk Helsenett SF and contributors
 * See the file CONTRIBUTORS for details.
 * 
 * This file is licensed under the MIT license
 * available at https://raw.githubusercontent.com/helsenorge/Helsenorge.Messaging/master/LICENSE
 */

using System;

namespace Helsenorge.Messaging.Amqp.Exceptions
{
    /// <summary>
    /// The exception that is thrown when the peer cannot send a frame because the smallest encoding of the performative
    /// with the currently valid values would be too large to fit within a frame of the agreed maximum frame size.
    /// </summary>
    public class FrameSizeTooSmallException : AmqpException
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="FrameSizeTooSmallException"/> class.
        /// </summary>
        /// <param name="message">The error message that explains the reason for this exception.</param>
        public FrameSizeTooSmallException(string message)
            : this(message, null)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="FrameSizeTooSmallException"/> class.
        /// </summary>
        /// <param name="message">The error message that explains the reason for this exception.</param>
        /// <param name="innerException">The exception that is the cause of the current exception, or a null reference if no inner exception is specified.</param>
        public FrameSizeTooSmallException(string message, Exception innerException)
            : base(message, innerException)
        {
        }

        /// <inheritdoc cref="AmqpException.CanRetry"/>
        public override bool CanRetry => false;
    }
}
