/*
 * SonarC#
 * Copyright (C) 2014-2017 SonarSource SA
 * mailto:info AT sonarsource DOT com
 *
 * This program is free software; you can redistribute it and/or
 * modify it under the terms of the GNU Lesser General Public
 * License as published by the Free Software Foundation; either
 * version 3 of the License, or (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU
 * Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with this program; if not, write to the Free Software Foundation,
 * Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.
 */
package org.sonar.plugins.csharp;

import java.util.Collections;
import java.util.List;
import org.sonar.api.PropertyType;
import org.sonar.api.config.PropertyDefinition;
import org.sonar.api.resources.Qualifiers;

public class CSharpMsBuildIntegrationProvider {

  private static final String CATEGORY = "Scanner for MSBuild";

  private CSharpMsBuildIntegrationProvider() {
  }

  public static List extensions() {
    return Collections.singletonList(
      PropertyDefinition.builder("sonar.cs.msbuild.testProjectPattern")
        .name("Test project pattern")
        .description("Regular expression matched by test project files path (.NET syntax)")
        .defaultValue("[^\\\\]*test[^\\\\]*$")
        .category(CATEGORY)
        .onQualifiers(Qualifiers.PROJECT)
        .type(PropertyType.STRING)
        .build());
  }

}
