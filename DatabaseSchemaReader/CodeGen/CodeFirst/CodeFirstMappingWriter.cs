﻿using System;
using System.Globalization;
using System.Linq;
using System.Text;
using DatabaseSchemaReader.DataSchema;

namespace DatabaseSchemaReader.CodeGen.CodeFirst
{
    class CodeFirstMappingWriter
    {
        //http://msdn.microsoft.com/en-us/library/hh295844%28v=vs.103%29.aspx

        private readonly DatabaseTable _table;
        private readonly CodeWriterSettings _codeWriterSettings;
        private readonly MappingNamer _mappingNamer;
        private readonly ClassBuilder _cb;

        public CodeFirstMappingWriter(DatabaseTable table, CodeWriterSettings codeWriterSettings, MappingNamer mappingNamer)
        {
            if (table == null) throw new ArgumentNullException("table");
            if (mappingNamer == null) throw new ArgumentNullException("mappingNamer");

            _codeWriterSettings = codeWriterSettings;
            _mappingNamer = mappingNamer;
            _table = table;
            _cb = new ClassBuilder();
        }

        /// <summary>
        /// Gets the name of the mapping class.
        /// </summary>
        /// <value>
        /// The name of the mapping class.
        /// </value>
        public string MappingClassName { get; private set; }

        public string Write()
        {
            _cb.AppendLine("using System.ComponentModel.DataAnnotations;");
            if (_table.PrimaryKeyColumn != null && !_table.PrimaryKeyColumn.IsIdentity)
            {
                //in EF v5 DatabaseGeneratedOption is in DataAnnotations.Schema
                _cb.AppendLine("using System.ComponentModel.DataAnnotations.Schema;");
            }
            _cb.AppendLine("using System.Data.Entity.ModelConfiguration;");

            MappingClassName = _mappingNamer.NameMappingClass(_table.NetName);

            using (_cb.BeginNest("namespace " + _codeWriterSettings.Namespace + ".Mapping"))
            {
                using (_cb.BeginNest("public class " + MappingClassName + " : EntityTypeConfiguration<" + _table.NetName + ">", "Class mapping to " + _table.Name + " table"))
                {
                    using (_cb.BeginNest("public " + MappingClassName + "()", "Constructor"))
                    {
                        MapTableName();

                        AddPrimaryKey();

                        _cb.AppendLine("// Properties");
                        WriteColumns();

                        _cb.AppendLine("// Navigation properties");
                        foreach (var foreignKeyChild in _table.ForeignKeyChildren)
                        {
                            WriteForeignKeyCollection(foreignKeyChild);
                        }
                    }
                }
            }

            return _cb.ToString();
        }

        private void MapTableName()
        {
            //NB CodeFirst automatically pluralizes (PluralizingTableNameConvention)
            //If the name is singular in database, it won't work...
            //if (_table.Name == _table.NetName) return;

            //Safer to always specify table name!

            var name = _table.Name;
            _cb.AppendLine("//table");
            if (!string.IsNullOrEmpty(_table.SchemaOwner) && _table.SchemaOwner != "dbo")
            {
                _cb.AppendFormat("ToTable(\"{0}\", \"{1}\");", name, _table.SchemaOwner);
            }
            else
            {
                _cb.AppendFormat("ToTable(\"{0}\");", name);
            }
        }

        private void AddPrimaryKey()
        {
            if (_table.PrimaryKey == null || _table.PrimaryKey.Columns.Count == 0)
            {
                if (_table is DatabaseView)
                {
                    AddCompositePrimaryKeyForView();
                    return;
                }
                _cb.AppendLine("//TODO- you MUST add a primary key!");
                return;
            }
            if (_table.HasCompositeKey)
            {
                AddCompositePrimaryKey();
                return;
            }

            var idColumn = _table.PrimaryKeyColumn;
            //in case PrepareSchemaNames.Prepare(schema) not done
            var netName = idColumn.NetName ?? idColumn.Name;

            //IdKeyDiscoveryConvention: "Id" or class"Id" is default
            if (netName.Equals("Id", StringComparison.OrdinalIgnoreCase))
                return;
            if (netName.Equals(_table.NetName + "Id", StringComparison.OrdinalIgnoreCase))
                return;

            _cb.AppendLine("// Primary key");
            _cb.AppendLine("HasKey(x => x." + netName + ");");
        }

        private void AddCompositePrimaryKey()
        {
            var keys = string.Join(", ",
                    _table.Columns
                    .Where(x => x.IsPrimaryKey)
                //primary keys must be scalar so if it's a foreign key use the Id mirror property
                    .Select(x => "x." + x.NetName + (x.IsForeignKey ? "Id" : string.Empty))
                    .ToArray());
            _cb.AppendLine("// Primary key (composite)");
            //double braces for a format
            _cb.AppendFormat("HasKey(x => new {{ {0} }});", keys);
        }
        private void AddCompositePrimaryKeyForView()
        {
            //we make all the non-nullable columns as keys. 
            //Nullable pks make EF die (EntityKey.AddHashValue NullReference)
            var candidatePrimaryKeys = _table.Columns.Where(x => !x.Nullable).ToArray();
            if (!candidatePrimaryKeys.Any())
            {
                candidatePrimaryKeys = _table.Columns.ToArray();
                _cb.AppendLine("// Warning: nullable columns may cause EntityKey errors. Try AsNoTracking()");
            }
            var keys = string.Join(", ",
                    candidatePrimaryKeys
                //primary keys must be scalar so if it's a foreign key use the Id mirror property
                    .Select(x => "x." + x.NetName + (x.IsForeignKey ? "Id" : string.Empty))
                    .ToArray());
            _cb.AppendLine("// Primary key (composite for view)");
            //double braces for a format
            _cb.AppendFormat("HasKey(x => new {{ {0} }});", keys);
        }

        private void WriteColumns()
        {
            //map the columns
            foreach (var column in _table.Columns)
            {
                WriteColumn(column);
            }
        }

        private void WriteColumn(DatabaseColumn column)
        {
            if (column.IsForeignKey)
            {
                WriteForeignKey(column);
                return;
            }

            var propertyName = column.NetName;
            if (string.IsNullOrEmpty(propertyName)) propertyName = column.Name;
            var sb = new StringBuilder();
            if (column.IsPrimaryKey)
            {
                //let's comment it to make it explicit
                _cb.AppendLine("//  " + propertyName + " is primary key" +
                    ((column.IsIdentity) ? " (identity)" : ""));
            }

            sb.AppendFormat(CultureInfo.InvariantCulture, "Property(x => x.{0})", propertyName);
            if (propertyName != column.Name)
            {
                sb.AppendFormat(CultureInfo.InvariantCulture, ".HasColumnName(\"{0}\")", column.Name);
            }
            if (column.IsPrimaryKey && !column.IsIdentity)
            {
                //assumed to be identity by default
                sb.AppendFormat(CultureInfo.InvariantCulture,
                                ".HasDatabaseGeneratedOption(DatabaseGeneratedOption.None)");
            }

            WriteColumnType(column, sb);

            if (!column.Nullable)
            {
                sb.Append(".IsRequired()");
            }

            sb.Append(";");
            _cb.AppendLine(sb.ToString());
        }

        private static void WriteColumnType(DatabaseColumn column, StringBuilder sb)
        {
            var dt = column.DataType;
            if (dt == null)
            {
                //we don't know the type, so state it explicitly
                sb.AppendFormat(CultureInfo.InvariantCulture,
                                ".HasColumnType(\"{0}\")",
                                column.DbDataType);
                return;
            }
            //nvarchar(max) may be -1
            if (dt.IsStringClob)
            {
                sb.Append(".IsMaxLength()");
                return;
            }
            if (dt.IsString)
            {
                if (column.Length == -1 || column.Length >= 1073741823)
                {
                    //MaxLength (and Text/Ntext/Clob) should be marked explicitly
                    sb.Append(".IsMaxLength()");
                }
                else if (column.Length > 0)
                {
                    //otherwise specify an explicit max size
                    sb.AppendFormat(CultureInfo.InvariantCulture, ".HasMaxLength({0})",
                                    column.Length.GetValueOrDefault());
                }
                return;
            }
            if (dt.TypeName.Equals("money", StringComparison.OrdinalIgnoreCase))
            {
                sb.Append(".HasColumnType(\"money\")");
                return;
            }
            if (dt.IsNumeric && !dt.IsInt && !dt.IsFloat && column.Precision.HasValue) //decimal
            {
                if (column.Precision != 18 || column.Scale != 0)
                {
                    sb.AppendFormat(CultureInfo.InvariantCulture, ".HasPrecision({0}, {1})",
                                    column.Precision.GetValueOrDefault(),
                                    column.Scale.GetValueOrDefault());
                    return;
                }
            }
            //special types (SQLServer only for now) that can be explicitly mapped
            if (dt.TypeName.Equals("image", StringComparison.OrdinalIgnoreCase))
            {
                sb.Append(".HasColumnType(\"image\")");
                return;
            }
            if (dt.TypeName.Equals("timestamp", StringComparison.OrdinalIgnoreCase))
            {
                sb.Append(
                    ".IsConcurrencyToken().HasColumnType(\"timestamp\").HasDatabaseGeneratedOption(DatabaseGeneratedOption.Computed)");
            }
        }

        private void WriteForeignKey(DatabaseColumn column)
        {
            var propertyName = column.NetName;
            var sb = new StringBuilder();
            sb.AppendFormat(CultureInfo.InvariantCulture, "Has{0}(x => x.{1})",
                column.Nullable ? "Optional" : "Required",
                propertyName);
            //then map the inverse with our foreign key children convention
            sb.AppendFormat(CultureInfo.InvariantCulture, ".WithMany(c => c.{0})", _codeWriterSettings.NameCollection(column.Table.NetName));
            if (column.IsPrimaryKey || _codeWriterSettings.UseForeignKeyIdProperties)
            {
                //for pk/fk we have a mirror property
                //TODO: don't use Id here
                var fkIdName = propertyName + "Id";
                _cb.AppendFormat("Property(x => x.{0}).HasColumnName(\"{1}\");", fkIdName, column.Name);
                sb.AppendFormat(CultureInfo.InvariantCulture, ".HasForeignKey(c => c.{0})", fkIdName);
            }
            else
            {
                //otherwise specify the underlying column name
                sb.AppendFormat(CultureInfo.InvariantCulture, ".Map(m => m.MapKey(\"{0}\"))", column.Name);
            }
            //could look up cascade rule here
            sb.Append(";");
            _cb.AppendLine(sb.ToString());
        }

        private void WriteForeignKeyCollection(DatabaseTable foreignKeyChild)
        {
            if (foreignKeyChild.IsManyToManyTable())
            {
                WriteManyToManyForeignKeyCollection(foreignKeyChild);
                return;
            }

            var foreignKeyTable = foreignKeyChild.Name;
            var childClass = foreignKeyChild.NetName;
            var foreignKey = foreignKeyChild.ForeignKeys.FirstOrDefault(fk => fk.RefersToTable == _table.Name);
            if (foreignKey == null) return; //corruption in our database
            //we won't deal with composite keys
            //var fkColumn = foreignKey.Columns.FirstOrDefault();

            _cb.AppendFormat("//Foreign key to {0} ({1})", foreignKeyTable, childClass);
            var propertyName = _codeWriterSettings.NameCollection(childClass);

            var sb = new StringBuilder();
            sb.AppendFormat(CultureInfo.InvariantCulture, "HasMany(x => x.{0})", propertyName);
            //specify the opposite direction? Probably not needed

            sb.Append(";");
            _cb.AppendLine(sb.ToString());
        }

        private void WriteManyToManyForeignKeyCollection(DatabaseTable foreignKeyChild)
        {
            var otherEnd = foreignKeyChild.ManyToManyTraversal(_table);
            _cb.AppendLine("// Many to many foreign key to " + otherEnd.Name);
            var childClass = otherEnd.NetName;
            var propertyName = _codeWriterSettings.NameCollection(childClass);
            var reverseName = _codeWriterSettings.NameCollection(_table.NetName);

            var sb = new StringBuilder();
            sb.AppendFormat(CultureInfo.InvariantCulture, "HasMany(x => x.{0})", propertyName);
            sb.AppendFormat(CultureInfo.InvariantCulture, ".WithMany(z => z.{0})", reverseName);
            _cb.AppendLine(sb.ToString());
            using (_cb.BeginBrace(".Map(map => "))
            {
                _cb.AppendLine("map.ToTable(\"" + foreignKeyChild.Name + "\");");
                //left key = HasMany side
                var cols = foreignKeyChild.ForeignKeys
                    .First(x => x.RefersToTable == _table.Name)
                    .Columns.Select(x => '"' + x + '"')
                    .ToArray();
                var leftColumns = string.Join(", ", cols);
                _cb.AppendLine("map.MapLeftKey(" + leftColumns + ");");
                //right key = WithMany side
                cols = foreignKeyChild.ForeignKeys
                    .First(x => x.RefersToTable == otherEnd.Name)
                    .Columns.Select(x => '"' + x + '"')
                    .ToArray();
                var rightColumns = string.Join(", ", cols);
                _cb.AppendLine("map.MapRightKey(" + rightColumns + ");");
            }

            _cb.AppendLine(");");

        }
    }
}
