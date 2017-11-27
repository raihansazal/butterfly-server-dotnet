﻿/*
 * Copyright 2017 Fireshark Studios, LLC
 * 
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 * 
 *     http://www.apache.org/licenses/LICENSE-2.0
 * 
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
*/

using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using Butterfly.Database.Event;

using Dict = System.Collections.Generic.Dictionary<string, object>;

namespace Butterfly.Database {

    public interface ITransaction : IDisposable {
        Task BeginAsync();

        Task<bool> CreateAsync(CreateStatement statement);

        Task<object> InsertAsync(string statementSql, dynamic statementParams, bool ignoreIfDuplicate = false);
        Task<object> InsertAsync(InsertStatement statement, dynamic statementParams, bool ignoreIfDuplicate = false);

        Task<int> UpdateAsync(string statementSql, dynamic statementParams);
        Task<int> UpdateAsync(UpdateStatement statement, dynamic statementParams);

        Task<int> DeleteAsync(string statementSql, dynamic statementParams);
        Task<int> DeleteAsync(DeleteStatement statement, dynamic statementParams);

        Task TruncateAsync(string tableName);

        Task CommitAsync();

        void Rollback();
    }

    public abstract class BaseTransaction : ITransaction {
        protected readonly Database database;
        protected readonly List<ChangeDataEvent> changeDataEvents = new List<ChangeDataEvent>();

        public BaseTransaction(Database database) {
            this.database = database;
        }

        // Create methods
        public async Task<bool> CreateAsync(CreateStatement statement) {
            return await this.DoCreateAsync(statement);
        }

        protected abstract Task<bool> DoCreateAsync(CreateStatement statement);

        // Insert methods
        public async Task<object> InsertAsync(string statementSql, dynamic statementParams, bool ignoreIfDuplicate = false) {
            InsertStatement statement = new InsertStatement(this.database, statementSql);
            return await this.InsertAsync(statement, statementParams, ignoreIfDuplicate: ignoreIfDuplicate);
        }

        public async Task<object> InsertAsync(InsertStatement statement, dynamic statementParams, bool ignoreIfDuplicate = false) {
            Dict statementParamsDict = statement.ConvertParamsToDict(statementParams);
            Dict statementParamsDictWithDefaults = this.database.ApplyDefaultValues(statement.TableRefs[0].table, statementParamsDict);
            (string executableSql, Dict executableParams) = statement.GetExecutableSqlAndParams(statementParamsDictWithDefaults);

            Func<object> getGeneratedId;
            try {
                getGeneratedId = await this.DoInsertAsync(executableSql, executableParams, ignoreIfDuplicate);
            }
            catch (DuplicateKeyDatabaseException e) {
                if (ignoreIfDuplicate) return null;
                throw;
            }

            object generatedId = statement.TableRefs[0].table.AutoIncrementFieldName!=null && getGeneratedId != null ? getGeneratedId() : null;

            // Generate change data event
            Dict record;
            if (generatedId != null) {
                record = new Dict(statementParamsDictWithDefaults) {
                    [statement.TableRefs[0].table.AutoIncrementFieldName] = generatedId
                };
            }
            else {
                record = statementParamsDictWithDefaults;
            }
            ChangeDataEvent changeDataEvent = new ChangeDataEvent(DataEventType.Insert, statement.TableRefs[0].table.Name, record);
            this.changeDataEvents.Add(changeDataEvent);

            // Return primary key value
            return generatedId != null ? generatedId : Database.GetKeyValue(statement.TableRefs[0].table.PrimaryIndex.FieldNames, statementParamsDictWithDefaults);
        }

        protected abstract Task<Func<object>> DoInsertAsync(string executableSql, Dict executableParams, bool ignoreIfDuplicate);


        // Update methods
        public async Task<int> UpdateAsync(string statementSql, dynamic statementParams) {
            UpdateStatement statement = new UpdateStatement(this.database, statementSql);
            return await this.UpdateAsync(statement, statementParams);
        }

        public async Task<int> UpdateAsync(UpdateStatement statement, dynamic statementParams) {
            Dict statementParamsDict = statement.ConvertParamsToDict(statementParams);
            statement.ConfirmAllParamsUsed(statementParamsDict);
            (string executableSql, Dict executableParams) = statement.GetExecutableSqlAndParams(statementParamsDict);
            int count = await this.DoUpdateAsync(executableSql, executableParams);
            this.changeDataEvents.Add(new ChangeDataEvent(DataEventType.Update, statement.TableRefs[0].table.Name, statementParamsDict));
            return count;
        }

        protected abstract Task<int> DoUpdateAsync(string executableSql, Dict executableParams);

        // Delete methods
        public async Task<int> DeleteAsync(string sql, dynamic statementParams) {
            DeleteStatement statement = new DeleteStatement(this.database, sql);
            return await this.DeleteAsync(statement, statementParams);
        }

        public async Task<int> DeleteAsync(DeleteStatement statement, dynamic statementParams) {
            Dict statementParamsDict = statement.ConvertParamsToDict(statementParams);
            statement.ConfirmAllParamsUsed(statementParamsDict);
            (string executableSql, Dict executableParams) = statement.GetExecutableSqlAndParams(statementParamsDict);
            int count = await this.DoDeleteAsync(executableSql, executableParams);
            this.changeDataEvents.Add(new ChangeDataEvent(DataEventType.Delete, statement.TableRefs[0].table.Name, statementParamsDict));
            return count;
        }

        protected abstract Task<int> DoDeleteAsync(string executableSql, Dict executableParams);


        // Truncate methods
        public async Task TruncateAsync(string tableName) {
            await this.DoTruncateAsync(tableName);
        }

        protected abstract Task DoTruncateAsync(string tableName);

        public abstract Task BeginAsync();


        // Commit methods
        public async Task CommitAsync() {
            DataEventTransaction dataEventTransaction = this.changeDataEvents.Count > 0 ? new DataEventTransaction(DateTime.Now, this.changeDataEvents.ToArray()) : null;
            if (dataEventTransaction!=null) {
                await this.database.ProcessDataEventTransaction(TransactionState.Uncommitted, dataEventTransaction);
            }
            await this.DoCommit();
            if (dataEventTransaction!=null) {
                await this.database.ProcessDataEventTransaction(TransactionState.Committed, dataEventTransaction);
            }
        }

        protected abstract Task DoCommit();

        // Rollback methods
        public void Rollback() {
            this.DoRollback();
        }

        protected abstract void DoRollback();


        // Dispose methods
        public abstract void Dispose();

    }
}