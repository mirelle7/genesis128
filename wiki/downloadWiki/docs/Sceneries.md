Sceneries define the general settings of the map: the climate, textures, tree types, etc.

The sceneries are saved as XML-files in the _Sceneries_ folder. To create a new scenery, simply create a new file in there or copy and adjust one of the existing files.

An example scenery definition looks like this:

{code:xml}
<?xml version="1.0" encoding="utf-8" ?>
<Scenery Name="Snow" Weather="Snow">
  <BaseTexture Name="SnowLargeType1" Size="384"/>
  <OverlayTextures>
    <Texture Name="SnowType5" Size="256" Granularity="0.05"/>
    <Texture Name="SnowType6" Size="256" Granularity="0.1"/>
  </OverlayTextures>
  <CliffTexture Name="CliffMediumType17bsnowy" Size="256"/>
  <CliffOverlayTextures>
    <Texture Name="CliffMediumType17b" Size="256" Granularity="0.23"/>
  </CliffOverlayTextures>
  <Trees>
    <Tree>CWCnatTreePine3snow</Tree>
    <Tree>CWCnatTreeDogwood1Snow</Tree>
    <Tree>CWCnatTreeSpruce03Snow</Tree>
    <Tree>CWCnatTreeSpruce04Snow</Tree>
    <Tree>CWCnatTreeSpruce05Snow</Tree>
  </Trees>
  <Rocks>
    <Rock>Rocks8</Rock>
    <Rock>Rocks9</Rock>
    <Rock>Rocks10</Rock>
  </Rocks>
  <Road>TwoLane</Road>
</Scenery>
{code:c#}

* The _Name_ attribute of the Scenery tag can be chosen freely.

* The _Weather_ attribute can be either _Normal_ or _Snow_. If you use _Snow_, all objects will use their snow textures (if applicable).

* There has to be one _BaseTexture_ element.
	* The _Name_ must be the name of the texture.
	* _Size_ must be set to the side length of the texture in pixels. If you’re not sure about the size, extract the texture from the _Terrain.big_ or _TerrainZH.big_ files and open it in a graphics program.

* There can be multiple _Texture_ elements in the _OverlayTextures_ element.
	* Overlay textures are painted over the base texture like small "stains".
	* Each texture has to has its _Name_ and _Size_ set just like the base texture.
	* The _Granularity_ determines the size of the stains. The smaller the granularity value, the larger the stains will be.

* Just like there is a _BaseTexture_ and multiple _OverlayTextures_, you can set a _CliffTexture_ and _CliffOverlayTextures_. These will be used for the generated mountains.

* You can add multiple _Tree_ elements in the _Trees_ element.
	* Each tree must have its _Name_ set to the name of the object type.
	* You can add the same tree multiple times to increase the probability of this tree to be chosen.

* Defining rocks works just like defining trees.

* In the _Road_ element, you can specify the road type that should be used in the map.
