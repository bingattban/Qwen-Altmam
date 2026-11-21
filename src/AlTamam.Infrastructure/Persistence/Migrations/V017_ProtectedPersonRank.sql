-- V017: Preserve all existing persons, movement history, audit events and custom values.
-- Introduce rank as one protected fixed system column; rank values are keyed by PersonId
-- and editable only through the already audited optimistic-concurrency value service.
UPDATE CustomFieldDefinitions SET DisplayOrder=4
WHERE FieldDefinitionId='FLD-SYS-NAME' AND TechnicalKey='sys_person_name';
UPDATE CustomFieldDefinitions SET DisplayOrder=5
WHERE FieldDefinitionId='FLD-SYS-SITE' AND TechnicalKey='sys_site_name';
INSERT INTO CustomFieldDefinitions
  (FieldDefinitionId,TechnicalKey,ArabicCaption,FieldType,DisplayOrder,
   IsSystemProtected,VisibleFlag,ActiveFlag,CreatedAtUtc)
VALUES ('FLD-SYS-RANK','sys_rank','الرتبة',1,3,1,1,1,STRFTIME('%Y-%m-%dT%H:%M:%fZ','now'));
