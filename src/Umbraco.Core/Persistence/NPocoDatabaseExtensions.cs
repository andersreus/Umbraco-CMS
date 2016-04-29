﻿using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Data.SqlClient;
using System.Linq;
using System.Text.RegularExpressions;
using NPoco;
using Umbraco.Core.Persistence.SqlSyntax;

namespace Umbraco.Core.Persistence
{
    /// <summary>
    /// Provides extension methods to NPoco Database class.
    /// </summary>
    public static class NPocoDatabaseExtensions
    {
        // NOTE
        //
        // proper way to do it with TSQL and SQLCE
        //   IF EXISTS (SELECT ... FROM table WITH (UPDLOCK,HOLDLOCK)) WHERE ...)
        //   BEGIN
        //     UPDATE table SET ... WHERE ...
        //   END
        //   ELSE
        //   BEGIN
        //     INSERT INTO table (...) VALUES (...)
        //   END
        //
        // works in READ COMMITED, TSQL & SQLCE lock the constraint even if it does not exist, so INSERT is OK
        //
        // proper way to do it with MySQL
        //   IF EXISTS (SELECT ... FROM table WHERE ... FOR UPDATE)
        //   BEGIN
        //     UPDATE table SET ... WHERE ...
        //   END
        //   ELSE
        //   BEGIN
        //     INSERT INTO table (...) VALUES (...)
        //   END
        //
        // MySQL locks the constraint ONLY if it exists, so INSERT may fail...
        //   in theory, happens in READ COMMITTED but not REPEATABLE READ
        //   http://www.percona.com/blog/2012/08/28/differences-between-read-committed-and-repeatable-read-transaction-isolation-levels/
        //   but according to
        //   http://dev.mysql.com/doc/refman/5.0/en/set-transaction.html
        //   it won't work for exact index value (only ranges) so really...
        //
        // MySQL should do
        //   INSERT INTO table (...) VALUES (...) ON DUPLICATE KEY UPDATE ...
        //
        // also the lock is released when the transaction is committed
        // not sure if that can have unexpected consequences on our code?
        //
        // so... for the time being, let's do with that somewhat crazy solution below...

        /// <summary>
        /// Safely inserts a record, or updates if it exists, based on a unique constraint.
        /// </summary>
        /// <param name="db"></param>
        /// <param name="poco"></param>
        /// <returns>The action that executed, either an insert or an update. If an insert occurred and a PK value got generated, the poco object
        /// passed in will contain the updated value.</returns>
        /// <remarks>
        /// <para>We cannot rely on database-specific options such as MySql ON DUPLICATE KEY UPDATE or MSSQL MERGE WHEN MATCHED because SQLCE
        /// does not support any of them. Ideally this should be achieved with proper transaction isolation levels but that would mean revisiting
        /// isolation levels globally. We want to keep it simple for the time being and manage it manually.</para>
        /// <para>We handle it by trying to update, then insert, etc. until something works, or we get bored.</para>
        /// <para>Note that with proper transactions, if T2 begins after T1 then we are sure that the database will contain T2's value
        /// once T1 and T2 have completed. Whereas here, it could contain T1's value.</para>
        /// </remarks>
        internal static RecordPersistenceType InsertOrUpdate<T>(this IDatabase db, T poco)
            where T : class
        {
            return db.InsertOrUpdate(poco, null, null);
        }

        /// <summary>
        /// Safely inserts a record, or updates if it exists, based on a unique constraint.
        /// </summary>
        /// <param name="db"></param>
        /// <param name="poco"></param>
        /// <param name="updateArgs"></param>
        /// <param name="updateCommand">If the entity has a composite key they you need to specify the update command explicitly</param>
        /// <returns>The action that executed, either an insert or an update. If an insert occurred and a PK value got generated, the poco object
        /// passed in will contain the updated value.</returns>
        /// <remarks>
        /// <para>We cannot rely on database-specific options such as MySql ON DUPLICATE KEY UPDATE or MSSQL MERGE WHEN MATCHED because SQLCE
        /// does not support any of them. Ideally this should be achieved with proper transaction isolation levels but that would mean revisiting
        /// isolation levels globally. We want to keep it simple for the time being and manage it manually.</para>
        /// <para>We handle it by trying to update, then insert, etc. until something works, or we get bored.</para>
        /// <para>Note that with proper transactions, if T2 begins after T1 then we are sure that the database will contain T2's value
        /// once T1 and T2 have completed. Whereas here, it could contain T1's value.</para>
        /// </remarks>
        internal static RecordPersistenceType InsertOrUpdate<T>(this IDatabase db,
            T poco,
            string updateCommand,
            object updateArgs)
            where T : class
        {
            if (poco == null)
                throw new ArgumentNullException("poco");

            // try to update
            var rowCount = updateCommand.IsNullOrWhiteSpace()
                    ? db.Update(poco)
                    : db.Update<T>(updateCommand, updateArgs);
            if (rowCount > 0)
                return RecordPersistenceType.Update;

            // failed: does not exist, need to insert
            // RC1 race cond here: another thread may insert a record with the same constraint

            var i = 0;
            while (i++ < 4)
            {
                try
                {
                    // try to insert
                    db.Insert(poco);
                    return RecordPersistenceType.Insert;
                }
                catch (SqlException) // TODO: need to find out if all db will throw that exception - probably OK
                {
                    // failed: exists (due to race cond RC1)
                    // RC2 race cond here: another thread may remove the record

                    // try to update
                    rowCount = updateCommand.IsNullOrWhiteSpace()
                        ? db.Update(poco)
                        : db.Update<T>(updateCommand, updateArgs);
                    if (rowCount > 0)
                        return RecordPersistenceType.Update;

                    // failed: does not exist (due to race cond RC2), need to insert
                    // loop
                }
            }

            // this can go on forever... have to break at some point and report an error.
            throw new DataException("Record could not be inserted or updated.");
        }

        /// <summary>
        /// This will escape single @ symbols for npoco values so it doesn't think it's a parameter
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        public static string EscapeAtSymbols(string value)
        {
            if (value.Contains("@"))
            {
                //this fancy regex will only match a single @ not a double, etc...
                var regex = new Regex("(?<!@)@(?!@)");
                return regex.Replace(value, "@@");
            }
            return value;

        }

        // fixme - NPoco has BulkInsert now?

        public static void BulkInsertRecords<T>(this IDatabase db, ISqlSyntaxProvider sqlSyntax, IEnumerable<T> collection)
        {
            //don't do anything if there are no records.
            if (collection.Any() == false)
                return;

            using (var tr = db.GetTransaction())
            {
                db.BulkInsertRecords(sqlSyntax, collection, tr, true);
            }
        }

        /// <summary>
        /// Performs the bulk insertion in the context of a current transaction with an optional parameter to complete the transaction
        /// when finished
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="db"></param>
        /// <param name="sqlSyntax"></param>
        /// <param name="collection"></param>
        /// <param name="tr"></param>
        /// <param name="commitTrans"></param>
        public static void BulkInsertRecords<T>(this IDatabase db, ISqlSyntaxProvider sqlSyntax, IEnumerable<T> collection, ITransaction tr, bool commitTrans = false)
        {
            //don't do anything if there are no records.
            if (collection.Any() == false)
                return;

            try
            {
                //if it is sql ce or it is a sql server version less than 2008, we need to do individual inserts.
                var sqlServerSyntax = sqlSyntax as SqlServerSyntaxProvider;
                if ((sqlServerSyntax != null && (int) sqlServerSyntax.ServerVersion.ProductVersionName < (int) SqlServerSyntaxProvider.VersionName.V2008)
                    || sqlSyntax is SqlCeSyntaxProvider)
                {
                    //SqlCe doesn't support bulk insert statements!

                    foreach (var poco in collection)
                    {
                        db.Insert(poco);
                    }
                }
                else
                {
                    string[] sqlStatements;
                    var cmds = db.GenerateBulkInsertCommand(collection, db.Connection, out sqlStatements);
                    for (var i = 0; i < sqlStatements.Length; i++)
                    {
                        using (var cmd = cmds[i])
                        {
                            cmd.CommandText = sqlStatements[i];
                            cmd.ExecuteNonQuery();
                        }
                    }
                }

                if (commitTrans)
                {
                    tr.Complete();
                }
            }
            catch
            {
                if (commitTrans)
                {
                    tr.Dispose();
                }
                throw;
            }
        }

        /// <summary>
        /// Creates a bulk insert command
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="db"></param>
        /// <param name="collection"></param>
        /// <param name="connection"></param>
        /// <param name="sql"></param>
        /// <returns>Sql commands with populated command parameters required to execute the sql statement</returns>
        /// <remarks>
        /// The limits for number of parameters are 2100 (in sql server, I think there's many more allowed in mysql). So
        /// we need to detect that many params and split somehow.
        /// For some reason the 2100 limit is not actually allowed even though the exception from sql server mentions 2100 as a max, perhaps it is 2099
        /// that is max. I've reduced it to 2000 anyways.
        /// </remarks>
        internal static IDbCommand[] GenerateBulkInsertCommand<T>(
            this IDatabase db,
            IEnumerable<T> collection,
            DbConnection connection,
            out string[] sql)
        {
            //A filter used below a few times to get all columns except result cols and not the primary key if it is auto-incremental
            Func<PocoData, KeyValuePair<string, PocoColumn>, bool> includeColumn = (data, column) =>
                {
                    if (column.Value.ResultColumn) return false;
                    if (data.TableInfo.AutoIncrement && column.Key == data.TableInfo.PrimaryKey) return false;
                    return true;
                };

            var pd = db.PocoDataFactory.ForType(typeof(T));
            var tableName = db.DatabaseType.EscapeTableName(pd.TableInfo.TableName);

            //get all columns to include and format for sql
            var cols = string.Join(", ",
                pd.Columns
                .Where(c => includeColumn(pd, c))
                .Select(c => tableName + "." + db.DatabaseType.EscapeSqlIdentifier(c.Key)).ToArray());

            var itemArray = collection.ToArray();

            //calculate number of parameters per item
            var paramsPerItem = pd.Columns.Count(i => includeColumn(pd, i));

            //Example calc:
            // Given: we have 4168 items in the itemArray, each item contains 8 command parameters (values to be inserterted)
            // 2100 / 8 = 262.5
            // Math.Floor(2100 / 8) = 262 items per trans
            // 4168 / 262 = 15.908... = there will be 16 trans in total

            //all items will be included if we have disabled db parameters
            var itemsPerTrans = Math.Floor(2000.00 / paramsPerItem);
            //there will only be one transaction if we have disabled db parameters
            var numTrans = Math.Ceiling(itemArray.Length / itemsPerTrans);

            var sqlQueries = new List<string>();
            var commands = new List<IDbCommand>();

            for (var tIndex = 0; tIndex < numTrans; tIndex++)
            {
                var itemsForTrans = itemArray
                    .Skip(tIndex * (int)itemsPerTrans)
                    .Take((int)itemsPerTrans);

                var cmd = db.CreateCommand(connection, "");
                var pocoValues = new List<string>();
                var index = 0;
                foreach (var poco in itemsForTrans)
                {
                    var values = new List<string>();
                    //get all columns except result cols and not the primary key if it is auto-incremental
                    var prefix = db.DatabaseType.GetParameterPrefix(cmd.Connection.ConnectionString);
                    foreach (var i in pd.Columns.Where(x => includeColumn(pd, x)))
                    {
                        db.AddParameter(cmd, i.Value.GetValue(poco));
                        values.Add(prefix + index++);
                    }
                    pocoValues.Add("(" + string.Join(",", values.ToArray()) + ")");
                }

                var sqlResult = string.Format("INSERT INTO {0} ({1}) VALUES {2}", tableName, cols, string.Join(", ", pocoValues));
                sqlQueries.Add(sqlResult);
                commands.Add(cmd);
            }

            sql = sqlQueries.ToArray();

            return commands.ToArray();
        }

        public static void TruncateTable(this IDatabase db, ISqlSyntaxProvider sqlSyntax, string tableName)
        {
            var sql = new Sql(string.Format(
                sqlSyntax.TruncateTable,
                sqlSyntax.GetQuotedTableName(tableName)));
            db.Execute(sql);
        }

        public static IsolationLevel GetCurrentTransactionIsolationLevel(this IDatabase database)
        {
            var transaction = database.Transaction;
            return transaction == null ? IsolationLevel.Unspecified : transaction.IsolationLevel;
        }

        public static IEnumerable<TResult> FetchByGroups<TResult, TSource>(this IDatabase db, IEnumerable<TSource> source, int groupSize, Func<IEnumerable<TSource>, Sql<SqlContext>> sqlFactory)
        {
            return source.SelectByGroups(x => db.Fetch<TResult>(sqlFactory(x)), groupSize);
        }

    }
}