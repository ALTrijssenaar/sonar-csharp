/*
 * SonarQube .NET Tests Library
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
package org.sonar.plugins.dotnet.tests;

import com.google.common.annotations.VisibleForTesting;
import java.io.File;
import org.sonar.api.batch.bootstrap.ProjectDefinition;
import org.sonar.api.batch.sensor.Sensor;
import org.sonar.api.batch.sensor.SensorContext;
import org.sonar.api.batch.sensor.SensorDescriptor;
import org.sonar.api.measures.CoreMetrics;
import org.sonar.api.utils.log.Logger;
import org.sonar.api.utils.log.Loggers;

public class UnitTestResultsImportSensor implements Sensor {

  private static final Logger LOG = Loggers.get(CoverageReportImportSensor.class);

  private final WildcardPatternFileProvider wildcardPatternFileProvider = new WildcardPatternFileProvider(new File("."), File.separator);
  private final UnitTestResultsAggregator unitTestResultsAggregator;
  private final ProjectDefinition projectDef;

  public UnitTestResultsImportSensor(UnitTestResultsAggregator unitTestResultsAggregator, ProjectDefinition projectDef) {
    this.unitTestResultsAggregator = unitTestResultsAggregator;
    this.projectDef = projectDef;
  }

  @Override
  public void describe(SensorDescriptor descriptor) {
    descriptor.name("Unit Test Results Import");
  }

  @Override
  public void execute(SensorContext context) {
    if (!unitTestResultsAggregator.hasUnitTestResultsProperty()) {
      LOG.debug("No unit test results property. Skip Sensor");
      return;
    }
    if (projectDef.getParent() == null) {
      analyze(context, new UnitTestResults());
    }
  }

  @VisibleForTesting
  void analyze(SensorContext context, UnitTestResults unitTestResults) {
    UnitTestResults aggregatedResults = unitTestResultsAggregator.aggregate(wildcardPatternFileProvider, unitTestResults);

    context.<Integer>newMeasure()
      .forMetric(CoreMetrics.TESTS)
      .on(context.module())
      .withValue(aggregatedResults.tests())
      .save();
    context.<Integer>newMeasure()
      .forMetric(CoreMetrics.TEST_ERRORS)
      .on(context.module())
      .withValue(aggregatedResults.errors())
      .save();
    context.<Integer>newMeasure()
      .forMetric(CoreMetrics.TEST_FAILURES)
      .on(context.module())
      .withValue(aggregatedResults.failures())
      .save();
    context.<Integer>newMeasure()
      .forMetric(CoreMetrics.SKIPPED_TESTS)
      .on(context.module())
      .withValue(aggregatedResults.skipped())
      .save();

    Long executionTime = aggregatedResults.executionTime();
    if (executionTime != null) {
      context.<Long>newMeasure()
        .forMetric(CoreMetrics.TEST_EXECUTION_TIME)
        .on(context.module())
        .withValue(executionTime)
        .save();
    }

    if (aggregatedResults.tests() > 0) {
      context.<Double>newMeasure()
        .forMetric(CoreMetrics.TEST_SUCCESS_DENSITY)
        .on(context.module())
        .withValue(aggregatedResults.passedPercentage())
        .save();
    }
  }

}
