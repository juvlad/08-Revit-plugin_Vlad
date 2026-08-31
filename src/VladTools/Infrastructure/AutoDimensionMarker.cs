using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;

namespace VladTools.Infrastructure
{
    /// <summary>
    /// Метка «этот размер поставила кнопка "Авторазмеры"» — без неё повторный запуск
    /// на тех же помещениях удваивал бы размеры, а размеры, поставленные пользователем
    /// руками, метки не имеют и не трогаются никогда.
    ///
    /// Схема <c>ExtensibleStorage</c> хранит помещение (по <c>UniqueId</c> — переживает
    /// перестроение модели, в отличие от <c>ElementId</c>) и номер нитки; по паре
    /// «помещение + нитка» старый размер находится и удаляется перед тем, как встать новому.
    /// </summary>
    internal static class AutoDimensionMarker
    {
        // Свой, зафиксированный раз и навсегда GUID — вторая сборка не должна завести другую схему.
        private static readonly Guid SchemaGuid = new Guid("6E2F9B0C-6C2E-4B7A-9E7A-6D9C2E3F4A11");

        private const string SchemaName = "VladToolsAutoDimension";
        private const string VendorId = "VLADTOOLS";
        private const string RoomField = "RoomUniqueId";
        private const string ChainField = "ChainIndex";

        /// <summary>Ставит метку на только что созданный размер. Вызывается внутри той же транзакции.</summary>
        public static void Mark(Dimension dimension, string roomUniqueId, int chainIndex)
        {
            var schema = GetOrCreateSchema();
            var entity = new Entity(schema);
            entity.Set(schema.GetField(RoomField), roomUniqueId ?? string.Empty);
            entity.Set(schema.GetField(ChainField), chainIndex);
            dimension.SetEntity(entity);
        }

        /// <summary>
        /// Все размеры этой схемы на активном виде, чьё помещение входит в обрабатываемый набор.
        /// Схемы ещё нет (кнопку никто не запускал) — пустой список, а не ошибка.
        /// </summary>
        public static List<ElementId> FindMarked(Document doc, ElementId viewId, ISet<string> roomUniqueIds)
        {
            var result = new List<ElementId>();

            var schema = Schema.Lookup(SchemaGuid);
            if (schema == null)
                return result;

            var dimensions = new FilteredElementCollector(doc, viewId)
                .OfClass(typeof(Dimension))
                .Cast<Dimension>();

            foreach (var dimension in dimensions)
            {
                string roomUniqueId;
                int chainIndex;

                if (TryRead(dimension, schema, out roomUniqueId, out chainIndex) && roomUniqueIds.Contains(roomUniqueId))
                    result.Add(dimension.Id);
            }

            return result;
        }

        private static bool TryRead(Dimension dimension, Schema schema, out string roomUniqueId, out int chainIndex)
        {
            roomUniqueId = string.Empty;
            chainIndex = -1;

            try
            {
                var entity = dimension.GetEntity(schema);
                if (entity == null || !entity.IsValid())
                    return false;

                roomUniqueId = entity.Get<string>(schema.GetField(RoomField)) ?? string.Empty;
                chainIndex = entity.Get<int>(schema.GetField(ChainField));
                return roomUniqueId.Length > 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static Schema GetOrCreateSchema()
        {
            var existing = Schema.Lookup(SchemaGuid);
            if (existing != null)
                return existing;

            var builder = new SchemaBuilder(SchemaGuid);
            builder.SetSchemaName(SchemaName);
            builder.SetVendorId(VendorId);
            builder.SetReadAccessLevel(AccessLevel.Public);
            builder.SetWriteAccessLevel(AccessLevel.Public);
            builder.AddSimpleField(RoomField, typeof(string));
            builder.AddSimpleField(ChainField, typeof(int));
            return builder.Finish();
        }
    }
}
