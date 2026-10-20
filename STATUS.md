# BlueStar + Manifest Hub: Estado de Integración

## 1. Misión y Repositorios
- **Manifest Hub**: `d:\BlueStarMANI` (Rama: `main`, Commit: `a134a31998fcae0ad051d720a964bb1dbd879fe8`)
- **BlueStar**: `C:\Users\Valen\Documents\BLUESTAR 1.3` (Rama: `feat/manifest-hub-integration`, Commit: `4a47fda16391411cf3d0f4493513aa395a0ef95c`)
- **DepotDownloaderMod**: `C:\Users\Valen\Documents\BLUESTAR 1.3\src\BlueStar.DepotDownloader` (Rama: `master`, Commit: `8ab4d51cc02e1cb04d99e303062fd2a0b6535a2b`)

## 2. Matriz de Requisitos vs. Estado de Componentes (Hito 0 - Auditoría Consolidada)

| Requisito / Dimensión | Estado Previo | Defecto / Evidencia | Corrección Acordada |
| :--- | :---: | :--- | :--- |
| **1. Modelo canónico y fechas verificadas** | ⚠️ Parcial | `BuildSnapshot.Timestamp` default `UtcNow`; `PatchDate` cae a `UtcNow` si `CreationTime` es nulo (`BuildResolver.cs:155`). No hay distinción formal `Verified`, `Estimated`, `Unknown`. | Modelar `DateQuality` (`Verified`, `Estimated`, `Unknown`) y `SnapshotCompleteness` (`Complete`, `PartialDelta`, `Inferred`). Si no hay fecha auténtica, la fecha es `null` ("Fecha desconocida"). |
| **2. Índices y catálogo relacional** | ❌ Defectuoso | `UpdateDepotIndexAsync` (`ManifestStorageManager.cs:768`) graba `"appId": 0` en `depots.json`. Faltan esquemas v2 y hay duplicación `builds/` vs `snapshots/`. | Reparar asociación `AppId` en `depots.json`. Unificar persistencia de builds/snapshots con paginación e índices por branch. |
| **3. API y servicio de binarios en Hub** | ❌ Incompleto | `HubReceiverServer.cs` devuelve 404 para `/manifests/{depotId}/{manifestId}.manifest`. No hay endpoint de historial paginado, ni de snapshot por buildId, ni de diff. | Mapear `/manifests/...`, agregar `GET /api/v1/builds/{appId}/history`, `GET /api/v1/builds/{appId}/{buildId}`, y `GET /api/v1/builds/{appId}/compare`. |
| **4. Integridad de manifests y ledger** | ❌ Vulnerable | `ManifestValidator.cs:332` no valida IDs si no hay metadatos. `ContributionTransportService.cs:203` fabrica recibos de 100 puntos si la red falla. Falta ignorar `config/` en git. | Exigir metadatos y coincidencia de IDs en el validador. Eliminar fabricación de recibos en cliente. Agregar `config/` y `steam_auth.json` a `.gitignore`. Bloqueo atómico de ledger. |
| **5. Historial cronológico en BlueStar** | ❌ Desordenado | `BuildResolver.cs` concatena listas sin ordenar. La UI muestra builds desordenados y sin fechas auténticas. | Ordenar formalmente por `AuthenticDate` / `CreationTime` desc, luego `BuildId` desc. Mostrar badge de fecha auténtica vs desconocida. |
| **6. Inspección de composición de versión** | ❌ Caja negra | La UI solo dice "X depots" en snapshots de Hub. El usuario no puede ver qué depots/manifests componen la versión. | Crear modelo y vista de composición de versión (`VersionDepotDiffItem`) que exponga DepotId, Nombre, ManifestId actual y destino, y tamaño. |
| **7. Comparación de snapshots y plan incremental** | ⚠️ Desconectado | `DifferentialUpdateResolver.cs` e `InstallationPlanner.cs` existen en backend pero no se conectan con la UI de versiones ni el downloader. | Exponer la comparativa diferencial (Unchanged, Modified, Added, Removed) con ahorro de bytes. Generar y entregar plan incremental al downloader. |
| **8. Ejecución con DepotDownloaderMod** | ⚠️ Parcial | `DepotDownloaderProvider` soporta manifests locales en caché, pero no omite depots sin cambios (`InstalledManifestID == targetManifestId`) en actualizaciones. | Pre-resolver manifests destino en caché global (cero llamadas a Steam para manifests) y excluir depots sin cambios del request de descarga. |

## 3. Plan de Ejecución por Hitos
- **Hito 0:** Auditoría de repositorios y matriz de requisitos (**COMPLETADO**).
- **Hito 1:** Modelo canónico, contratos y migraciones (ManifestHub & BlueStar Core).
- **Hito 2:** Ingestión histórica, cobertura, validación rigurosa e integridad del ledger.
- **Hito 3:** Almacenamiento, índices corregidos y API completa en Hub.
- **Hito 4:** Interfaz de historial de versiones y composición de snapshots en BlueStar.
- **Hito 5:** Comparación diferencial y generación del plan de actualización.
- **Hito 6:** Integración y ejecución del plan incremental con DepotDownloaderMod.
- **Hito 7:** Auditoría final, pruebas de regresión, integración end-to-end y métricas.
