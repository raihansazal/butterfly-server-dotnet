﻿/* This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/. */

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Data;
using System.Data.Common;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using NLog;

using Butterfly.Core.Database;

using Dict = System.Collections.Generic.Dictionary<string, object>;

namespace Butterfly.SQLite {

    /// <inheritdoc/>
    public class SQLiteDatabase : BaseDatabase {

        private static readonly Logger logger = LogManager.GetCurrentClassLogger();

        public override bool CanJoin => true;

        public override bool CanFieldAlias => true;

        public SQLiteDatabase(string fileName) : base($"Data Source={fileName};Version=3;") {
            if (!File.Exists(fileName)) {
                SQLiteConnection.CreateFile(fileName);
            }
        }

        protected override void LoadSchema() {
            string commandText = "SELECT name FROM sqlite_master WHERE type='table';";
            using (var connection = new SQLiteConnection(this.ConnectionString)) {
                connection.Open();
                var command = new SQLiteCommand(commandText, connection);
                using (var reader = command.ExecuteReader()) {
                    while (reader.Read()) {
                        string tableName = reader[0].ToString();
                        Table table = this.LoadTableSchema(tableName);
                        this.tableByName[table.Name] = table;
                    }
                }
            }
        }

        protected override Table LoadTableSchema(string tableName) {
            TableFieldDef[] fieldDefs = this.GetFieldDefs(tableName);
            TableIndex[] indexes = this.GetIndexes(tableName);
            return new Table(tableName, fieldDefs, indexes);
        }

        protected TableFieldDef[] GetFieldDefs(string tableName) {
            List<TableFieldDef> fields = new List<TableFieldDef>();
            string commandText = $"SELECT * FROM {tableName} WHERE 1 = 2";
            using (var connection = new SQLiteConnection(this.ConnectionString)) {
                connection.OpenAsync();
                var command = new SQLiteCommand(commandText, connection);
                DataTable dataTable = new DataTable();
                using (var reader = command.ExecuteReader()) {
                    dataTable.Load(reader);
                }
                foreach (DataColumn dataColumn in dataTable.Columns) {
                    bool isAutoIncrement = dataColumn.DataType == typeof(long) && dataTable.PrimaryKey.Length==1 && dataTable.PrimaryKey[0].ColumnName == dataColumn.ColumnName;
                    TableFieldDef fieldDef = new TableFieldDef(dataColumn.ColumnName, dataColumn.DataType, dataColumn.MaxLength, dataColumn.AllowDBNull, isAutoIncrement);
                    fields.Add(fieldDef);
                }
            }
            return fields.ToArray();
        }

        protected TableIndex[] GetIndexes(string tableName) {
            string commandText = $"SELECT * FROM {tableName} WHERE 1 = 2";
            using (var connection = new SQLiteConnection(this.ConnectionString)) {
                connection.OpenAsync();
                var command = new SQLiteCommand(commandText, connection);
                DataTable dataTable = new DataTable();
                using (var reader = command.ExecuteReader()) {
                    dataTable.Load(reader);
                }
                return new TableIndex[] { new TableIndex(TableIndexType.Primary, dataTable.PrimaryKey.Select(x => x.ColumnName).ToArray()) };
            }
        }

        protected override BaseTransaction CreateTransaction() {
            return new SQLiteTransaction(this);
        }

        protected override async Task<Dict[]> DoSelectRowsAsync(string executableSql, Dict executableParams) {
            SelectStatement statement = new SelectStatement(this, executableSql);

            List<Dict> rows = new List<Dict>();
            try {
                using (var connection = new SQLiteConnection(this.ConnectionString)) {
                    await connection.OpenAsync();
                    var command = new SQLiteCommand(executableSql, connection);
                    foreach (var keyValuePair in executableParams) {
                        command.Parameters.AddWithValue(keyValuePair.Key, keyValuePair.Value);
                    }
                    using (var reader = await command.ExecuteReaderAsync()) {
                        ReadOnlyCollection<DbColumn> columns = null;
                        while (await reader.ReadAsync()) {
                            if (columns == null) columns = reader.GetColumnSchema();
                            Dict row = new Dictionary<string, object>();
                            foreach (var column in columns) {
                                row[column.ColumnName] = ConvertValue(reader[column.ColumnName]);
                            }
                            rows.Add(row);
                        }
                    }
                }
            }
            catch (Exception e) {
                logger.Error(e, $"Error executing {statement.Sql}...");
                throw;
            }

            return rows.ToArray();
        }

        protected override Task<Dict[]> DoQueryRowsAsync(string storedProcedureName, Dict vars = null) {
            throw new NotImplementedException();
        }

        protected static object ConvertValue(object value) {
            if (value == null || value == DBNull.Value) {
                return null;
            }
            else {
                return value;
            }
        }

    }
}
