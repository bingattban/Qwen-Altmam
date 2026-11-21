-- V017: rank is a fixed PERSON display column. Values are preserved in
-- audited CustomFieldValues and imported by the existing column-id-keyed XLSX flow.
-- Preserve a pre-existing visible "الرتبة" custom field and all its data.
UPDATE CustomFieldDefinitions SET DisplayOrder=DisplayOrder+1
WHERE ActiveFlag=1 AND IsSystemProtected=0
  AND NOT EXISTS(SELECT 1 FROM CustomFieldDefinitions
                 WHERE ArabicCaption='الرتبة' AND ActiveFlag=1 AND VisibleFlag=1);
INSERT INTO CustomFieldDefinitions(FieldDefinitionId,TechnicalKey,ArabicCaption,
  FieldType,DisplayOrder,IsSystemProtected,VisibleFlag,ActiveFlag,CreatedAtUtc)
SELECT 'FLD-CORE-RANK-017','system_rank','الرتبة',1,5,0,1,1,
  STRFTIME('%Y-%m-%dT%H:%M:%fZ','now')
WHERE NOT EXISTS(SELECT 1 FROM CustomFieldDefinitions
                 WHERE ArabicCaption='الرتبة' AND ActiveFlag=1 AND VisibleFlag=1);
