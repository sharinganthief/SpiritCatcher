﻿#if !UNITY_WEBPLAYER
// Note: This parital class is not compiled in for WebPlayer builds.
// The Unity Webplayer is deprecated. If you *must* use it then make sure Tiled2Unity assets are imported via another build target first.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using UnityEditor;
using UnityEngine;

namespace Tiled2Unity
{
    // Handles a Mesh being imported.
    // At this point we should have everything we need to build out any prefabs for the tiled map object
    partial class ImportTiled2Unity
    {
        // By the time this is called, our assets should be ready to create the map prefab
        public void MeshImported(string objPath)
        {
            string xmlPath = GetXmlImportAssetPath(objPath);

            ImportBehaviour importBehaviour = ImportBehaviour.FindOrCreateImportBehaviour(xmlPath);
            importBehaviour.IncrementProgressBar(String.Format("Create prefab: {0}", Path.GetFileNameWithoutExtension(GetPrefabAssetPath(objPath, false, null))));

            foreach (var xmlPrefab in importBehaviour.XmlDocument.Root.Elements("Prefab"))
            {
                
                CreatePrefab(xmlPrefab, objPath);
            }
        }

        private void CreatePrefab(XElement xmlPrefab, string objPath)
        {
            var customImporters = GetCustomImporterInstances();

            // Part 1: Create the prefab
            string prefabName = xmlPrefab.Attribute("Name").Value;
            float prefabScale = ImportUtils.GetAttributeAsFloat(xmlPrefab, "scale", 1.0f);
            GameObject tempPrefab = new GameObject(prefabName);
            HandleTiledAttributes(tempPrefab, xmlPrefab);
            HandleCustomProperties(tempPrefab, xmlPrefab, customImporters);

            // Part 2: Build out the prefab
            // We may have an 'isTrigger' attribute that we want our children to obey
            bool isTrigger = ImportUtils.GetAttributeAsBoolean(xmlPrefab, "isTrigger", false);
            AddGameObjectsTo(tempPrefab, xmlPrefab, isTrigger, objPath, customImporters);

            // Part 3: Allow for customization from other editor scripts to be made on the prefab
            // (These are generally for game-specific needs)
            CustomizePrefab(tempPrefab, customImporters);

            // Part 3.5: Apply the scale only after all children have been added
            tempPrefab.transform.localScale = new Vector3(prefabScale, prefabScale, prefabScale);

            // Part 4: Save the prefab, keeping references intact.
            string resourcePath = ImportUtils.GetAttributeAsString(xmlPrefab, "resourcePath", "");
            bool isResource = !String.IsNullOrEmpty(resourcePath) || ImportUtils.GetAttributeAsBoolean(xmlPrefab, "resource", false);
            string prefabPath = GetPrefabAssetPath(prefabName, isResource, resourcePath);
            UnityEngine.Object finalPrefab = AssetDatabase.LoadAssetAtPath(prefabPath, typeof(GameObject));

            if (finalPrefab == null)
            {
                // The prefab needs to be created
                ImportUtils.ReadyToWrite(prefabPath);
                finalPrefab = PrefabUtility.CreateEmptyPrefab(prefabPath);
            }

            // Replace the prefab, keeping connections based on Name.
            PrefabUtility.ReplacePrefab(tempPrefab, finalPrefab, ReplacePrefabOptions.ReplaceNameBased);

            // Destroy the instance from the current scene hiearchy.
            UnityEngine.Object.DestroyImmediate(tempPrefab);
        }

        private void AddGameObjectsTo(GameObject parent, XElement xml, bool isParentTrigger, string objPath, IList<ICustomTiledImporter> customImporters)
        {
            foreach (XElement goXml in xml.Elements("GameObject"))
            {
                string name = ImportUtils.GetAttributeAsString(goXml, "Name", "");
                string copyFrom = ImportUtils.GetAttributeAsString(goXml, "copy", "");

                GameObject child = null;
                if (!String.IsNullOrEmpty(copyFrom))
                {
                    float opacity = ImportUtils.GetAttributeAsFloat(goXml, "opacity", 1);
                    child = CreateCopyFromMeshObj(copyFrom, objPath, opacity);
                    if (child == null)
                    {
                        // We're in trouble. Errors should already be in the log.
                        return;
                    }

                    // Apply the sorting to the renderer of the mesh object we just copied into the child
                    Renderer renderer = child.GetComponent<Renderer>();

                    string sortingLayer = ImportUtils.GetAttributeAsString(goXml, "sortingLayerName", "");
                    if (!String.IsNullOrEmpty(sortingLayer) && !SortingLayerExposedEditor.GetSortingLayerNames().Contains(sortingLayer))
                    {
                        Debug.LogError(string.Format("Sorting Layer \"{0}\" does not exist. Check your Project Settings -> Tags and Layers", sortingLayer));
                        renderer.sortingLayerName = "Default";
                    }
                    else
                    {
                        renderer.sortingLayerName = sortingLayer;
                    }

                    // Set the sorting order
                    renderer.sortingOrder = ImportUtils.GetAttributeAsInt(goXml, "sortingOrder", 0);
                }
                else
                {
                    child = new GameObject();
                }

                if (!String.IsNullOrEmpty(name))
                {
                    child.name = name;
                }

                // Assign the child to the parent
                child.transform.parent = parent.transform;

                // Set the position
                float x = ImportUtils.GetAttributeAsFloat(goXml, "x", 0);
                float y = ImportUtils.GetAttributeAsFloat(goXml, "y", 0);
                child.transform.localPosition = new Vector3(x, y, 0);

                // Add any tile animators
                AddTileAnimatorsTo(child, goXml);

                // Do we have any collision data?
                // Check if we are setting 'isTrigger' for ourselves or for our childen
                bool isTrigger = ImportUtils.GetAttributeAsBoolean(goXml, "isTrigger", isParentTrigger);
                AddCollidersTo(child, isTrigger, goXml);

                // Do we have any children of our own?
                AddGameObjectsTo(child, goXml, isTrigger, objPath, customImporters);

                // Does this game object have a tag?
                AssignTagTo(child, goXml);

                // Does this game object have a layer?
                AssignLayerTo(child, goXml);

                // Are there any custom properties?
                HandleCustomProperties(child, goXml, customImporters);

                // Set scale and rotation *after* children are added otherwise Unity will have child+parent transform cancel each other out
                float sx = ImportUtils.GetAttributeAsFloat(goXml, "scaleX", 1.0f);
                float sy = ImportUtils.GetAttributeAsFloat(goXml, "scaleY", 1.0f);
                child.transform.localScale = new Vector3(sx, sy, 1.0f);

                // Set the rotation
                // Use negative rotation on the z component because of change in coordinate systems between Tiled and Unity
                Vector3 localRotation = new Vector3();
                localRotation.x = (ImportUtils.GetAttributeAsBoolean(goXml, "flipY", false) == true) ? 180.0f : 0.0f;
                localRotation.y = (ImportUtils.GetAttributeAsBoolean(goXml, "flipX", false) == true) ? 180.0f : 0.0f;
                localRotation.z = -ImportUtils.GetAttributeAsFloat(goXml, "rotation", 0);
                child.transform.eulerAngles = localRotation;
            }
        }

        private void AssignLayerTo(GameObject gameObject, XElement xml)
        {
            string layerName = ImportUtils.GetAttributeAsString(xml, "layer", "");
            if (String.IsNullOrEmpty(layerName))
                return;

            int layerId = LayerMask.NameToLayer(layerName);
            if (layerId == -1)
            {
                string msg = String.Format("Layer '{0}' is not defined for '{1}'. Check project settings in Edit->Project Settings->Tags & Layers",
                    layerName,
                    GetFullGameObjectName(gameObject.transform));
                Debug.LogError(msg);
                return;
            }

            // Set the layer on ourselves (and our children)
            AssignLayerIdTo(gameObject, layerId);
        }

        private void AssignLayerIdTo(GameObject gameObject, int layerId)
        {
            if (gameObject == null)
                return;

            gameObject.layer = layerId;

            foreach (Transform child in gameObject.transform)
            {
                if (child.gameObject == null)
                    continue;

                // Do not set the layerId on a child that has already had his layerId explicitly set
                if (child.gameObject.layer != 0)
                    continue;

                AssignLayerIdTo(child.gameObject, layerId);
            }
        }

        private void AssignTagTo(GameObject gameObject, XElement xml)
        {
            string tag = ImportUtils.GetAttributeAsString(xml, "tag", "");
            if (String.IsNullOrEmpty(tag))
                return;

            // Let the user know if the tag doesn't exist in our project sttings
            try
            {
                gameObject.tag = tag;
            }
            catch (UnityException)
            {
                string msg = String.Format("Tag '{0}' is not defined for '{1}'. Check project settings in Edit->Project Settings->Tags & Layers",
                    tag,
                    GetFullGameObjectName(gameObject.transform));
                Debug.LogError(msg);
            }
        }

        private string GetFullGameObjectName(Transform xform)
        {
            if (xform == null)
                return "";
            string parentName = GetFullGameObjectName(xform.parent);

            if (String.IsNullOrEmpty(parentName))
                return xform.name;

            return String.Format("{0}/{1}", parentName, xform.name);
        }

        private void AddCollidersTo(GameObject gameObject, bool isTrigger, XElement xml)
        {
            // Box colliders
            foreach (XElement xmlBoxCollider2D in xml.Elements("BoxCollider2D"))
            {
                BoxCollider2D collider = gameObject.AddComponent<BoxCollider2D>();
                collider.isTrigger = isTrigger;
                float width = ImportUtils.GetAttributeAsFloat(xmlBoxCollider2D, "width");
                float height = ImportUtils.GetAttributeAsFloat(xmlBoxCollider2D, "height");
                collider.size = new Vector2(width, height);
                collider.offset = new Vector2(width * 0.5f, -height * 0.5f);

                ImportUtils.ApplyColliderOffset(xmlBoxCollider2D, collider);
            }

            // Circle colliders
            foreach (XElement xmlCircleCollider2D in xml.Elements("CircleCollider2D"))
            {
                CircleCollider2D collider = gameObject.AddComponent<CircleCollider2D>();
                collider.isTrigger = isTrigger;
                float radius = ImportUtils.GetAttributeAsFloat(xmlCircleCollider2D, "radius");
                collider.radius = radius;
                collider.offset = new Vector2(radius, -radius);

                ImportUtils.ApplyColliderOffset(xmlCircleCollider2D, collider);
            }

            // Edge colliders
            foreach (XElement xmlEdgeCollider2D in xml.Elements("EdgeCollider2D"))
            {
                EdgeCollider2D collider = gameObject.AddComponent<EdgeCollider2D>();
                collider.isTrigger = isTrigger;
                string data = xmlEdgeCollider2D.Element("Points").Value;

                // The data looks like this:
                //  x0,y0 x1,y1 x2,y2 ...
                var points = from pt in data.Split(' ')
                             let x = Convert.ToSingle(pt.Split(',')[0])
                             let y = Convert.ToSingle(pt.Split(',')[1])
                             select new Vector2(x, y);

                collider.points = points.ToArray();

                ImportUtils.ApplyColliderOffset(xmlEdgeCollider2D, collider);
            }

            // Polygon colliders
            foreach (XElement xmlPolygonCollider2D in xml.Elements("PolygonCollider2D"))
            {
                PolygonCollider2D collider = gameObject.AddComponent<PolygonCollider2D>();
                collider.isTrigger = isTrigger;

                var paths = xmlPolygonCollider2D.Elements("Path").ToArray();
                collider.pathCount = paths.Count();

                for (int p = 0; p < collider.pathCount; ++p)
                {
                    string data = paths[p].Value;

                    // The data looks like this:
                    //  x0,y0 x1,y1 x2,y2 ...
                    var points = from pt in data.Split(' ')
                                 let x = Convert.ToSingle(pt.Split(',')[0])
                                 let y = Convert.ToSingle(pt.Split(',')[1])
                                 select new Vector2(x, y);

                    collider.SetPath(p, points.ToArray());
                }

                ImportUtils.ApplyColliderOffset(xmlPolygonCollider2D, collider);
            }
        }

        private GameObject CreateCopyFromMeshObj(string copyFromName, string objPath, float opacity)
        {
            // Find a matching game object within the mesh object and "copy" it
            // (In Unity terms, the Instantiated object is a copy)
            UnityEngine.Object[] objects = AssetDatabase.LoadAllAssetsAtPath(objPath);
            foreach (var obj in objects)
            {
                if (obj.name != copyFromName)
                    continue;

                // We have a match but is it a game object?
                GameObject gameObj = GameObject.Instantiate(obj) as GameObject;
                if (gameObj == null)
                    continue;

                // Add a component that will control our initial shader properties
                TiledInitialShaderProperties shaderProps = gameObj.AddComponent<TiledInitialShaderProperties>();
                shaderProps.InitialOpacity = opacity;

                // Reset the Name so it is not decorated by the Instantiate call
                gameObj.name = obj.name;
                return gameObj;
            }

            // If we're here then there's an error with the mesh Name
            Debug.LogError(String.Format("No mesh named '{0}' to copy from.", copyFromName));
            return null;
        }

        private void AddTileAnimatorsTo(GameObject gameObject, XElement goXml)
        {
            // This object will only visible for a given moment of time within an animation
            var animXml = goXml.Element("TileAnimator");
            if (animXml != null)
            {
                TileAnimator tileAnimator = gameObject.AddComponent<TileAnimator>();
                tileAnimator.StartTime = ImportUtils.GetAttributeAsInt(animXml, "startTimeMs") * 0.001f;
                tileAnimator.Duration = ImportUtils.GetAttributeAsInt(animXml, "durationMs") * 0.001f;
                tileAnimator.TotalAnimationTime = ImportUtils.GetAttributeAsInt(animXml, "fullTimeMs") * 0.001f;
            }
        }

        private void HandleTiledAttributes(GameObject gameObject, XElement goXml)
        {
            // Add the TiledMap component
            TiledMap map = gameObject.AddComponent<TiledMap>();
            try
            {
                map.NumTilesWide = ImportUtils.GetAttributeAsInt(goXml, "numTilesWide");
                map.NumTilesHigh = ImportUtils.GetAttributeAsInt(goXml, "numTilesHigh");
                map.TileWidth = ImportUtils.GetAttributeAsInt(goXml, "tileWidth");
                map.TileHeight = ImportUtils.GetAttributeAsInt(goXml, "tileHeight");
                map.ExportScale = ImportUtils.GetAttributeAsFloat(goXml, "exportScale");
                map.MapWidthInPixels = ImportUtils.GetAttributeAsInt(goXml, "mapWidthInPixels");
                map.MapHeightInPixels = ImportUtils.GetAttributeAsInt(goXml, "mapHeightInPixels");
            }
            catch
            {
                Debug.LogWarning(String.Format("Error adding TiledMap component. Are you using an old version of Tiled2Unity in your Unity project?"));
                GameObject.DestroyImmediate(map);
            }
        }

        private void HandleCustomProperties(GameObject gameObject, XElement goXml, IList<ICustomTiledImporter> importers)
        {
            var props = from p in goXml.Elements("Property")
                        select new { Name = p.Attribute("Name").Value, Value = p.Attribute("value").Value };

            if (props.Count() > 0)
            {
                var dictionary = props.OrderBy(p => p.Name).ToDictionary(p => p.Name, p => p.Value);
                foreach (ICustomTiledImporter importer in importers)
                {
                    importer.HandleCustomProperties(gameObject, dictionary);
                }
            }
        }

        private void CustomizePrefab(GameObject prefab, IList<ICustomTiledImporter> importers)
        {
            foreach (ICustomTiledImporter importer in importers)
            {
                importer.CustomizePrefab(prefab);
            }
        }

        private IList<ICustomTiledImporter> GetCustomImporterInstances()
        {
            // Report an error for ICustomTiledImporter classes that don't have the CustomTiledImporterAttribute
            var errorTypes = from a in AppDomain.CurrentDomain.GetAssemblies()
                             from t in a.GetTypes()
                             where typeof(ICustomTiledImporter).IsAssignableFrom(t)
                             where !t.IsAbstract
                             where System.Attribute.GetCustomAttribute(t, typeof(CustomTiledImporterAttribute)) == null
                             select t;
            foreach (var t in errorTypes)
            {
                Debug.LogError(String.Format("ICustomTiledImporter type '{0}' is missing CustomTiledImporterAttribute", t));
            }

            // Find all the types with the CustomTiledImporterAttribute, instantiate them, and give them a chance to customize our prefab
            var types = from a in AppDomain.CurrentDomain.GetAssemblies()
                        from t in a.GetTypes()
                        where typeof(ICustomTiledImporter).IsAssignableFrom(t)
                        where !t.IsAbstract
                        from attr in System.Attribute.GetCustomAttributes(t, typeof(CustomTiledImporterAttribute))
                        let custom = attr as CustomTiledImporterAttribute
                        orderby custom.Order
                        select t;

            var instances = types.Select(t => (ICustomTiledImporter)Activator.CreateInstance(t));
            return instances.ToList();
        }

    } // end class
} // end namespace
#endif