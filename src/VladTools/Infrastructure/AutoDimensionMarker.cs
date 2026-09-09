using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;

namespace VladTools.Infrastructure
{
    /// <summary>
    /// The mark "this dimension was placed by the Auto Dimensions button" — without it a second run
    /// over the same rooms would double the dimensions, while dimensions the user placed by hand
    /// carry no mark and are never touched.
    ///
    /// The <c>ExtensibleStorage</c> schema stores the room (by <c>UniqueId</c> — it survives a model
    /// rebuild, unlike <c>ElementId</c>) and the chain index; the "room + chain" pair is what finds
    /// the old dimension so it can be deleted before the new one is placed.
    /// </summary>
    internal static class AutoDimensionMarker
    {
        // Our own GUID, fixed once and for all — a second build must not create a different schema.
        private static readonly Guid SchemaGuid = new Guid("6E2F9B0C-6C2E-4B7A-9E7A-6D9C2E3F4A11");

        private const string SchemaName = "VladToolsAutoDimension";
        private const string VendorId = "VLADTOOLS";
        private const string RoomField = "RoomUniqueId";
        private const string ChainField = "ChainIndex";

        /// <summary>Marks a dimension that has just been created. Called inside the same transaction.</summary>
        public static void Mark(Dimension dimension, string roomUniqueId, int chainIndex)
        {
            var schema = GetOrCreateSchema();
            var entity = new Entity(schema);
            entity.Set(schema.GetField(RoomField), roomUniqueId ?? string.Empty);
            entity.Set(schema.GetField(ChainField), chainIndex);
            dimension.SetEntity(entity);
        }

        /// <summary>
        /// Every dimension of this schema on the active view whose room is part of the set being
        /// processed. If the schema does not exist yet (nobody has run the button) the result is an
        /// empty list, not an error.
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
