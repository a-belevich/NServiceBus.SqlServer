namespace NServiceBus.Transports.SQLServer
{
    using System;
    using System.Data.SqlClient;
    using System.Transactions;
    using NServiceBus.Pipeline.Contexts;
    using IsolationLevel = System.Data.IsolationLevel;

    class NativeTransactionReceiveBehavior : ReceiveBehavior
    {
        readonly string connectionString;
        readonly TableBasedQueue errorQueue;
        readonly IsolationLevel isolationLevel;

        public NativeTransactionReceiveBehavior(string connectionString, TableBasedQueue errorQueue, TransactionOptions transactionOptions)
        {
            this.connectionString = connectionString;
            this.errorQueue = errorQueue;
            isolationLevel = GetSqlIsolationLevel(transactionOptions.IsolationLevel);
        }

        protected override void Invoke(IncomingContext context, Action<IncomingMessage> onMessage)
        {
            var queue = context.Get<TableBasedQueue>();
            var messageAvailabilitySignaller = context.Get<IMessageAvailabilitySignaller>();

            using (var connection = new SqlConnection(connectionString))
            {
                connection.Open();
                using (context.SetConnection(connectionString, connection))
                {
                    using (var transaction = connection.BeginTransaction(isolationLevel))
                    {
                        using (context.SetTransaction(connectionString, transaction))
                        {
                            MessageReadResult readResult;
                            try
                            {
                                readResult = queue.TryReceive(connection, transaction);
                            }
                            catch (Exception)
                            {
                                transaction.Rollback();
                                throw;
                            }
                            if (readResult.IsPoison)
                            {
                                errorQueue.Send(readResult.DataRecord, connection, transaction);
                                transaction.Commit();
                                return;
                            }
                            if (!readResult.Successful)
                            {
                                transaction.Commit();
                                return;
                            }

                            messageAvailabilitySignaller.MessageAvailable();

                            try
                            {
                                onMessage(readResult.Message);
                                transaction.Commit();
                            }
                            catch (Exception)
                            {
                                transaction.Rollback();
                                throw;
                            }
                        }
                    }
                }
            }
        }


        static IsolationLevel GetSqlIsolationLevel(System.Transactions.IsolationLevel isolationLevel)
        {
            switch (isolationLevel)
            {
                case System.Transactions.IsolationLevel.Serializable:
                    return IsolationLevel.Serializable;
                case System.Transactions.IsolationLevel.RepeatableRead:
                    return IsolationLevel.RepeatableRead;
                case System.Transactions.IsolationLevel.ReadCommitted:
                    return IsolationLevel.ReadCommitted;
                case System.Transactions.IsolationLevel.ReadUncommitted:
                    return IsolationLevel.ReadUncommitted;
                case System.Transactions.IsolationLevel.Snapshot:
                    return IsolationLevel.Snapshot;
                case System.Transactions.IsolationLevel.Chaos:
                    return IsolationLevel.Chaos;
                case System.Transactions.IsolationLevel.Unspecified:
                    return IsolationLevel.Unspecified;
            }

            return IsolationLevel.ReadCommitted;
        }
    }
}